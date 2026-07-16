import numpy as np
from sklearn.cluster import AgglomerativeClustering
from sklearn.metrics import silhouette_score
from sklearn.manifold import MDS
import sys
import warnings

# Filter sklearn FutureWarnings
warnings.filterwarnings('ignore', category=FutureWarning, module='sklearn')

class SpeakerClusteringEngine:
    # If k_new < established_k, we require the score improvement to exceed this
    # margin before accepting the reduction. Prevents a 0.002-score noise spike
    # from collapsing 3 speakers back to 2.
    K_REDUCTION_MARGIN = 0.04

    # When scores for k and k+1 differ by less than this, prefer the higher k.
    # "When in doubt, split" - false splits can be recovered by post-merge,
    # false merges cannot be recovered at all.
    SPLIT_PREFERENCE_MARGIN = 0.02

    def __init__(self):
        self.persistent_id_counter = 1
        self.speaker_colors = ["#4A90E2", "#F5A623", "#7ED321", "#BD10E0", "#9013FE", "#50E3C2"]
        # Highest k ever confirmed with enough confidence. Only increases, never decreases
        # unless there is overwhelming evidence (score gap > K_REDUCTION_MARGIN).
        self.established_k = 1
        # Tracks how many consecutive cycles each speaker has been a singleton (count=1).
        # A lingering singleton that never accumulates more segments is likely a noise artifact
        # (background music vocal, brief echo) rather than a real speaker.
        self._singleton_age: dict = {}

    def process(self, segment_registry, expected_speakers, lc_gate_func=None):
        """
        Process segment registry to cluster embeddings and assign persistent UUIDs.
        Returns a dictionary containing the updated state.
        lc_gate_func: callable(seg_end, next_seg_start) -> 'allow' | 'suppress' | 'reinforce'
        """
        n_segments = len(segment_registry)
        embeddings = np.array([seg['embedding'] for seg in segment_registry])
        
        if n_segments == 1 or expected_speakers == 1:
            labels = np.zeros(n_segments, dtype=int)
        else:
            similarity = np.dot(embeddings, embeddings.T)
            similarity = np.clip(similarity, -1.0, 1.0)
            cosine_distance = 1.0 - similarity
            
            # Apply LiveCaption Control Signals
            if lc_gate_func:
                for i in range(n_segments - 1):
                    seg_end = segment_registry[i]['end']
                    next_start = segment_registry[i+1]['start']
                    action = lc_gate_func(seg_end, next_start)
                    if action == 'suppress':
                        cosine_distance[i, i+1] = 0.0
                        cosine_distance[i+1, i] = 0.0
                    elif action == 'reinforce':
                        cosine_distance[i, i+1] = 1.0
                        cosine_distance[i+1, i] = 1.0
            
            try:
                # Force expected cluster count if specified (>1)
                if expected_speakers > 1:
                    n_clust = min(expected_speakers, n_segments)
                    clustering = AgglomerativeClustering(
                        n_clusters=n_clust,
                        metric='precomputed',
                        linkage='average'
                    )
                    labels = clustering.fit_predict(cosine_distance)
                else:
                    # SMART AUTO-ESTIMATE ENGINE (Silhouette Score)
                    max_dist = np.max(cosine_distance)
                    
                    if max_dist < 0.35:
                        labels = np.zeros(n_segments, dtype=int)
                    else:
                        best_k = 2
                        best_score = -1.0
                        best_labels = None
                        
                        max_k = min(n_segments - 1, 6) # Assume max 6 speakers
                        if max_k < 2:
                            max_k = 2
                            
                        if n_segments == 2:
                            if max_dist > 0.25:
                                labels = np.array([0, 1])
                            else:
                                labels = np.zeros(n_segments, dtype=int)
                        else:
                            all_scores = {}  # k -> (score, labels)
                            for k in range(2, max_k + 1):
                                clustering = AgglomerativeClustering(
                                    n_clusters=k,
                                    metric='precomputed',
                                    linkage='average'
                                )
                                tmp_labels = clustering.fit_predict(cosine_distance)
                                try:
                                    score = silhouette_score(cosine_distance, tmp_labels, metric='precomputed')
                                    print(f"   [Auto-Estimate] k={k} -> Silhouette Score: {score:.4f}")
                                except ValueError:
                                    score = -1.0
                                all_scores[k] = (score, tmp_labels)

                            # --- Split-preference tie-breaking ---
                            # Walk from high k downward; only drop to lower k if it beats
                            # the next higher k by more than SPLIT_PREFERENCE_MARGIN.
                            # This ensures "when scores are close, prefer more speakers".
                            sorted_ks = sorted(all_scores.keys(), reverse=True)
                            chosen_k = sorted_ks[0]  # start from highest k
                            chosen_score = all_scores[chosen_k][0]
                            for k in sorted_ks[1:]:
                                s = all_scores[k][0]
                                if s > chosen_score + self.SPLIT_PREFERENCE_MARGIN:
                                    # Lower k wins by a clear margin - accept it
                                    chosen_k = k
                                    chosen_score = s
                                # else: keep higher k (split preference)

                            # --- K-Reduction Hysteresis ---
                            # CORE-04: Clamp established_k to max_k before lookup.
                            # If established_k=6 but current max_k=3, all_scores only has
                            # keys {2..3}, so `6 in all_scores` is always False and the
                            # entire guard is silently bypassed.
                            effective_established_k = min(self.established_k, max_k)
                            if chosen_k < effective_established_k and effective_established_k in all_scores:
                                established_score = all_scores[effective_established_k][0]
                                gain = chosen_score - established_score
                                if gain < self.K_REDUCTION_MARGIN:
                                    print(f"   [HYSTERESIS] Proposed k={chosen_k} (score={chosen_score:.4f}) vs "
                                          f"established k={effective_established_k} (score={established_score:.4f}), "
                                          f"gain={gain:.4f} < {self.K_REDUCTION_MARGIN} -> keeping k={effective_established_k}")
                                    chosen_k = effective_established_k
                                    chosen_score = established_score

                            best_k = chosen_k
                            best_score = all_scores[best_k][0]
                            best_labels = all_scores[best_k][1]

                            # --- Update established_k using RAW argmax, not split-preference result ---
                            # chosen_k is biased upward by split-preference tie-breaking: when k=4 and k=5
                            # differ by only 0.006, k=5 wins by tie-break. Using chosen_k to update
                            # established_k causes a ratchet effect - established_k climbs to 5 even though
                            # k=4 has the highest actual Silhouette score.
                            #
                            # Fix: track established_k from raw_best_k (argmax silhouette), and only
                            # promote if raw_best_k clearly beats raw_best_k-1 by > SPLIT_PREFERENCE_MARGIN.
                            # This ensures established_k reflects genuine evidence, not tie-break artifacts.
                            raw_best_k = max(all_scores, key=lambda k: all_scores[k][0])
                            raw_best_score = all_scores[raw_best_k][0]

                            if raw_best_score > 0.15:
                                # Only promote if raw_best_k clearly beats the k below it
                                prev_k = raw_best_k - 1
                                if prev_k in all_scores:
                                    prev_score = all_scores[prev_k][0]
                                    if raw_best_score - prev_score >= self.SPLIT_PREFERENCE_MARGIN:
                                        self.established_k = max(self.established_k, raw_best_k)
                                    # else: raw_best_k only barely beat prev_k - don't promote
                                else:
                                    # No lower k to compare against (raw_best_k is lowest possible)
                                    self.established_k = max(self.established_k, raw_best_k)

                            if best_labels is not None:
                                labels = best_labels
                                print(f"[CLUSTERING] Optimal speakers count found: {best_k} (Score: {best_score:.4f}, established_k={self.established_k})")
                            else:
                                labels = np.zeros(n_segments, dtype=int)
            except Exception as e:
                print(f"Clustering error: {e}", file=sys.stderr)
                labels = np.zeros(n_segments, dtype=int)
                
        # --- Pre-calculate Natural Variance for Dynamic Thresholding ---
        # Measure intra-cluster spread (how compact each cluster is).
        # Use np.std instead of np.mean to capture the actual dispersion,
        # not the average distance which collapses toward 0 as N grows.
        intra_variances = []
        cluster_centroids_by_label = {}
        for c in np.unique(labels):
            indices = np.where(labels == c)[0]
            if len(indices) > 0:
                cluster_embeddings = [segment_registry[idx]['embedding'] for idx in indices]
                centroid = np.mean(cluster_embeddings, axis=0)
                centroid = centroid / (np.linalg.norm(centroid) + 1e-9)  # re-normalize
                cluster_centroids_by_label[c] = centroid
                dists = [1.0 - np.clip(np.dot(emb, centroid), -1.0, 1.0) for emb in cluster_embeddings]
                if len(dists) > 1:
                    intra_variances.append(np.std(dists))  # std, not mean
                    
        natural_variance = np.mean(intra_variances) if intra_variances else 0.1
        dynamic_threshold = max(natural_variance * 2.5, 0.35) 
        
        # --- Label Alignment (Persistent UUID tracking) ---
        weights = {}
        for i, c in enumerate(labels):
            old_uid = segment_registry[i].get('uuid')
            if old_uid:
                weights[(c, old_uid)] = weights.get((c, old_uid), 0) + 1
                
        assigned_pids = set()
        cluster_to_uuid = {}
        
        sorted_weights = sorted(weights.items(), key=lambda item: item[1], reverse=True)
        for (c, uid), count in sorted_weights:
            if c not in cluster_to_uuid and uid not in assigned_pids:
                cluster_to_uuid[c] = uid
                assigned_pids.add(uid)
                
        orphan_clusters = []
        for c in np.unique(labels):
            if c not in cluster_to_uuid:
                orphan_clusters.append(c)
                
        if orphan_clusters and self.persistent_id_counter > 1:
            known_profiles = {}
            for i, seg in enumerate(segment_registry):
                uid = seg.get('uuid')
                if uid:
                    if uid not in known_profiles:
                        known_profiles[uid] = []
                    known_profiles[uid].append(seg['embedding'])
                    
            # CORE-01: np.mean of L2-normalized vectors is NOT unit-norm.
            # Re-normalize so that dot-product == cosine similarity.
            def _normalized_mean(embs):
                m = np.mean(embs, axis=0)
                n = np.linalg.norm(m)
                return m / n if n > 1e-9 else m
            known_centroids = {uid: _normalized_mean(embs) for uid, embs in known_profiles.items()}
            
            for c in orphan_clusters:
                indices = np.where(labels == c)[0]
                orphan_embs = [segment_registry[i]['embedding'] for i in indices]
                orphan_centroid = np.mean(orphan_embs, axis=0)
                
                best_match = None
                best_dist = float('inf')
                
                for uid, k_cent in known_centroids.items():
                    sim = np.dot(orphan_centroid, k_cent)
                    dist = 1.0 - np.clip(sim, -1.0, 1.0)
                    if dist < best_dist:
                        best_dist = dist
                        best_match = uid
                        
                if best_match is not None and best_dist < dynamic_threshold:
                    # --- Second-pass safety check for orphan matching ---
                    # tight_match_threshold uses a floor of 0.25 so that normal
                    # intra-speaker vocal variation (emotional speech, voice acting)
                    # is NOT mistaken for a new speaker. CAM++ intra-speaker range
                    # for a single person is typically 0.05-0.22 depending on content.
                    # Without the floor, a compact cluster (natural_variance~0.02) sets
                    # tight_thresh=0.03, triggering false new-speaker creation at dist=0.25.
                    tight_match_threshold = max(natural_variance * 1.5, 0.25)

                    if best_dist < tight_match_threshold:
                        cluster_to_uuid[c] = best_match
                        assigned_pids.add(best_match)
                    else:
                        # Genuinely new speaker: too far from all known centroids.
                        print(f"   [ORPHAN] Cluster {c} dist={best_dist:.3f} to {best_match} "
                              f"exceeds tight threshold {tight_match_threshold:.3f} -> new speaker.")
                        new_id = f"Speaker-{self.persistent_id_counter:02d}"
                        self.persistent_id_counter += 1
                        cluster_to_uuid[c] = new_id
                else:
                    new_id = f"Speaker-{self.persistent_id_counter:02d}"
                    self.persistent_id_counter += 1
                    cluster_to_uuid[c] = new_id
                    
        for c in np.unique(labels):
            if c not in cluster_to_uuid:
                new_id = f"Speaker-{self.persistent_id_counter:02d}"
                self.persistent_id_counter += 1
                cluster_to_uuid[c] = new_id
                
        # 4. Apply persistent IDs back to registry
        for i, c in enumerate(labels):
            new_uid = cluster_to_uuid[c]
            segment_registry[i]['uuid'] = new_uid
            
        # --- Update Speaker Profiles & Find Best Reference Audio ---
        profiles = {}
        for seg in segment_registry:
            uid = seg.get('uuid')
            if not uid: continue
            if uid not in profiles:
                profiles[uid] = {'embeddings': [], 'segments': []}
            profiles[uid]['embeddings'].append(seg['embedding'])
            profiles[uid]['segments'].append(seg)
            
        centroids = {}
        speaker_profiles_data = {}
        for uid, data in profiles.items():
            raw_centroid = np.mean(data['embeddings'], axis=0)
            # CORE-01: Re-normalize centroid to unit sphere so dot-product stays
            # a proper cosine similarity measure in all downstream comparisons.
            norm = np.linalg.norm(raw_centroid)
            centroid = raw_centroid / norm if norm > 1e-9 else raw_centroid
            centroids[uid] = centroid
            
            best_dist = float('inf')
            best_audio = None
            for seg in data['segments']:
                sim = np.dot(seg['embedding'], centroid)
                dist = 1.0 - np.clip(sim, -1.0, 1.0)
                if dist < best_dist:
                    best_dist = dist
                    best_audio = seg.get('raw_audio') # Note: CLI might not have raw_audio
            
            speaker_profiles_data[uid] = {
                'centroid': centroid,
                'best_audio': best_audio,
                'count': len(data['segments'])
            }
            
        # --- Singleton Absorption Pass ---
        # A speaker with only 1 segment is treated as unconfirmed until it accumulates more.
        SINGLETON_ABSORB_THRESH   = 0.30
        PERSISTENT_ABSORB_THRESH  = 0.38
        SINGLETON_MAX_AGE         = 3    # cycles before switching to persistent threshold

        merged_any = False  # shared flag for singleton pass AND active post-merge below
        uids_snapshot = list(speaker_profiles_data.keys())
        for uid_s in uids_snapshot:
            if uid_s not in speaker_profiles_data:
                continue
            if speaker_profiles_data[uid_s]['count'] != 1:
                # Speaker has grown beyond singleton - clear its age record
                self._singleton_age.pop(uid_s, None)
                continue

            # Increment age counter for this singleton
            current_age = self._singleton_age.get(uid_s, 0) + 1
            absorb_thresh = SINGLETON_ABSORB_THRESH if current_age <= SINGLETON_MAX_AGE else PERSISTENT_ABSORB_THRESH

            # Find closest multi-segment speaker
            best_host = None
            best_host_dist = float('inf')
            for uid_h in speaker_profiles_data:
                if uid_h == uid_s or speaker_profiles_data[uid_h]['count'] <= 1:
                    continue
                sim = np.dot(speaker_profiles_data[uid_s]['centroid'],
                             speaker_profiles_data[uid_h]['centroid'])
                d = 1.0 - np.clip(sim, -1.0, 1.0)
                if d < best_host_dist:
                    best_host_dist = d
                    best_host = uid_h

            if best_host is not None and best_host_dist < absorb_thresh:
                age_tag = f"age={current_age}, persistent" if current_age > SINGLETON_MAX_AGE else f"age={current_age}"
                print(f"   [SINGLETON ABSORB] {uid_s} -> {best_host} "
                      f"(dist={best_host_dist:.3f} < thresh={absorb_thresh:.2f}, {age_tag})")
                for seg in segment_registry:
                    if seg.get('uuid') == uid_s:
                        seg['uuid'] = best_host
                del speaker_profiles_data[uid_s]
                speaker_profiles_data[best_host]['count'] += 1
                self._singleton_age.pop(uid_s, None)  # clean up absorbed speaker
                merged_any = True
            else:
                # Not absorbed this cycle - update age
                self._singleton_age[uid_s] = current_age
                if current_age > SINGLETON_MAX_AGE:
                    print(f"   [SINGLETON LINGERING] {uid_s} age={current_age}, "
                          f"dist={best_host_dist:.3f} to {best_host} (thresh={absorb_thresh:.2f}) - still waiting")

        # Clean up age records for speakers that no longer exist in profiles
        active_uids = set(speaker_profiles_data.keys())
        stale_uids = [uid for uid in self._singleton_age if uid not in active_uids]
        for uid in stale_uids:
            del self._singleton_age[uid]

        # --- Active Post-Merge (Global Fusion) ---
        INTRA_MERGE_RATIO = 2.0
        post_merge_thresh = natural_variance * INTRA_MERGE_RATIO
        
        uids = list(centroids.keys())
        for i in range(len(uids)):
            for j in range(i+1, len(uids)):
                uid_A = uids[i]
                uid_B = uids[j]
                
                if uid_A not in speaker_profiles_data or uid_B not in speaker_profiles_data:
                    continue
                    
                sim = np.dot(centroids[uid_A], centroids[uid_B])
                dist = 1.0 - np.clip(sim, -1.0, 1.0)
                
                if dist < post_merge_thresh:
                    count_A = speaker_profiles_data[uid_A]['count']
                    count_B = speaker_profiles_data[uid_B]['count']
                    
                    target, victim = (uid_A, uid_B) if count_A >= count_B else (uid_B, uid_A)
                    print(f"[ACTIVE POST-MERGE] {victim} -> {target} (dist: {dist:.3f} < thresh: {post_merge_thresh:.3f})")
                    
                    for seg in segment_registry:
                        if seg.get('uuid') == victim:
                            seg['uuid'] = target
                            
                    del speaker_profiles_data[victim]
                    merged_any = True
                    
        if merged_any:
            profiles = {}
            for seg in segment_registry:
                uid = seg.get('uuid')
                if uid not in profiles:
                    profiles[uid] = {'embeddings': [], 'segments': []}
                profiles[uid]['embeddings'].append(seg['embedding'])
                profiles[uid]['segments'].append(seg)
                
            speaker_profiles_data = {}
            for uid, data in profiles.items():
                raw_centroid = np.mean(data['embeddings'], axis=0)
                # CORE-01: Re-normalize after mean
                norm = np.linalg.norm(raw_centroid)
                centroid = raw_centroid / norm if norm > 1e-9 else raw_centroid

                # CORE-03: Find best reference audio
                best_dist = float('inf')
                best_audio = None
                for seg in data['segments']:
                    dist = 1.0 - np.clip(np.dot(seg['embedding'], centroid), -1.0, 1.0)
                    if dist < best_dist:
                        best_dist = dist
                        best_audio = seg.get('raw_audio')

                speaker_profiles_data[uid] = {
                    'centroid': centroid,
                    'best_audio': best_audio,
                    'count': len(data['segments'])
                }
            
        uids = list(speaker_profiles_data.keys())

        timeline_lines = []
        for seg in segment_registry:
            timeline_lines.append((seg['start'], seg['end'], seg['uuid']))

        # Prepare data for MDS Plot
        vis_nodes = []
        c_dist = None
        if len(uids) > 1:
            try:
                c_dist = np.zeros((len(uids), len(uids)))
                for i in range(len(uids)):
                    for j in range(len(uids)):
                        sim = np.dot(speaker_profiles_data[uids[i]]['centroid'], speaker_profiles_data[uids[j]]['centroid'])
                        c_dist[i, j] = 1.0 - np.clip(sim, -1.0, 1.0)

                mds = MDS(n_components=2, dissimilarity='precomputed', random_state=42)
                pos = mds.fit_transform(c_dist)

                pos_min, pos_max = pos.min(axis=0), pos.max(axis=0)
                if not np.allclose(pos_min, pos_max):
                    pos = (pos - pos_min) / (pos_max - pos_min + 1e-9)
                else:
                    pos = np.full_like(pos, 0.5)

                for idx, uid in enumerate(uids):
                    vis_nodes.append({
                        'uid': uid,
                        'x': pos[idx, 0],
                        'y': pos[idx, 1],
                        'count': speaker_profiles_data[uid]['count']
                    })
            except Exception as e:
                print(f"MDS Error: {e}")
                vis_nodes = []
                c_dist = None
        elif len(uids) == 1:
            uid = uids[0]
            vis_nodes.append({
                'uid': uid,
                'x': 0.5,
                'y': 0.5,
                'count': speaker_profiles_data[uid]['count']
            })
            c_dist = np.array([[0.0]])

        # --- State Monitoring Log ---
        self._log_state(uids, speaker_profiles_data, c_dist, natural_variance, dynamic_threshold)

        return {
            'segment_registry': segment_registry,
            'speaker_profiles_data': speaker_profiles_data,
            'persistent_id_counter': self.persistent_id_counter,
            'timeline_lines': timeline_lines,
            'vis_nodes': vis_nodes,
            'natural_variance': natural_variance,
            'dynamic_threshold': dynamic_threshold,
            'uids': uids,
            'c_dist': c_dist
        }

    def _log_state(self, uids, speaker_profiles_data, c_dist, natural_variance, dynamic_threshold):
        """
        Print a structured state snapshot to console after each clustering cycle.
        """
        current_snapshot = {uid: speaker_profiles_data[uid]['count'] for uid in uids}

        # Detect changes vs previous round
        prev = getattr(self, '_prev_snapshot', {})
        added   = [uid for uid in current_snapshot if uid not in prev]
        removed = [uid for uid in prev if uid not in current_snapshot]
        changed = [uid for uid in current_snapshot
                   if uid in prev and current_snapshot[uid] != prev[uid]]

        has_structural_change = bool(added or removed)
        has_any_change = has_structural_change or bool(changed)

        self._log_tick = getattr(self, '_log_tick', 0) + 1
        should_print = has_structural_change or (has_any_change and self._log_tick % 5 == 0)

        if not should_print:
            self._prev_snapshot = current_snapshot
            return

        SEP = '-' * 56
        print(SEP)

        # Delta header
        if added:
            print(f"  [+] NEW SPEAKER(S): {', '.join(added)}")
        if removed:
            print(f"  [-] MERGED/GONE:    {', '.join(removed)}")

        # Roster table
        print(f"  {'Speaker':<14} {'Segs':>5}  {'Intra-dist (to centroid)':}")
        for uid in uids:
            seg_count = speaker_profiles_data[uid]['count']
            change_marker = ""
            if uid in added:
                change_marker = " <NEW>"
            elif uid in changed:
                delta = current_snapshot[uid] - prev.get(uid, 0)
                change_marker = f" (+{delta} segs)" if delta > 0 else f" ({delta} segs)"
            print(f"  {uid:<14} {seg_count:>5}  {change_marker}")

        # Inter-speaker distance matrix
        if c_dist is not None and len(uids) >= 2:
            col_w = 9
            header = " " * 16 + "".join(f"{uid:>{col_w}}" for uid in uids)
            print(f"\n  Distance Matrix  (merge_thresh={natural_variance * 2.0:.3f}, dyn={dynamic_threshold:.3f})")
            print(f"  {header}")
            for i, uid_row in enumerate(uids):
                row_vals = "".join(f"{c_dist[i, j]:>{col_w}.3f}" for j in range(len(uids)))
                print(f"  {uid_row:<14}  {row_vals}")

            if len(uids) >= 2:
                min_dist = float('inf')
                min_pair = ("", "")
                for i in range(len(uids)):
                    for j in range(i + 1, len(uids)):
                        if c_dist[i, j] < min_dist:
                            min_dist = c_dist[i, j]
                            min_pair = (uids[i], uids[j])
                merge_thresh = natural_variance * 2.0
                risk = "RISK" if min_dist < merge_thresh * 1.5 else "ok"
                print(f"\n  Closest pair: {min_pair[0]} <-> {min_pair[1]}  dist={min_dist:.3f}  [{risk}]")
        elif len(uids) == 1:
            print(f"\n  [single speaker, no matrix]")

        print(SEP)
        self._prev_snapshot = current_snapshot
