import numpy as np
from sklearn.manifold import MDS
import sys
import warnings

warnings.filterwarnings('ignore', category=FutureWarning, module='sklearn')


class SpeakerClusteringEngine:
    """
    Online sticky speaker assignment — driven by live COMPARE logs.

    Evidence (logs.txt session 2026-08-10 ~14:16, CAM++ loopback, 2-person café):
      - Mint first split worked at best_d = 0.287
      - TURN-FLIP ignored clear nearest (e.g. Spk-01=0.130 SAME vs Spk-02=0.260
        still forced Spk-02) → 8+ wrong labels
      - Pure nearest matched the embedding signal; force-alternate destroyed it
      - CONSISTENCY reassignment rewrote history and polluted centroids

    Policy (no turn-flip, no reassignment of locked segs):
      1) Segment already has uuid → keep forever (sticky lock)
      2) No speakers yet → mint Speaker-01
      3) n_spk < max and best_d >= NEW_SPEAKER_MIN and dur >= SHORT_SEG
         → mint new speaker (first split uses centroid=only Spk-01)
      4) Else assign nearest centroid
      5) Tie-break only when margin (second_d - best_d) < CLEAR_MARGIN:
         keep last_uid if it is among the two nearest; else nearest
    """

    # Cosine distance = 1 - cos_sim. Tuned ONLY from COMPARE rows in logs.txt.
    # First successful mint: best_d=0.287 → threshold just below that.
    NEW_SPEAKER_MIN = 0.26
    # Clear winner when second_d - best_d >= this (log clear cases 0.05–0.22).
    CLEAR_MARGIN = 0.04
    # Never mint from tiny VAD chips (noisy embedding).
    SHORT_SEG_SEC = 0.85
    # Logging band only (not used to override nearest).
    SAME_SPEAKER_MAX = 0.18

    def __init__(self):
        self.persistent_id_counter = 1
        self.speaker_colors = [
            "#4A90E2", "#F5A623", "#7ED321", "#BD10E0", "#9013FE", "#50E3C2"
        ]
        self.established_k = 1
        self._singleton_age: dict = {}

    @staticmethod
    def _normalized_mean(embs):
        m = np.mean(embs, axis=0)
        n = np.linalg.norm(m)
        return m / n if n > 1e-9 else m

    @staticmethod
    def _norm_emb(emb):
        emb = np.asarray(emb, dtype=np.float64)
        n = np.linalg.norm(emb)
        return emb / n if n > 1e-9 else emb

    @staticmethod
    def _cos_dist(a, b):
        return 1.0 - float(np.clip(np.dot(a, b), -1.0, 1.0))

    def _mint_speaker(self):
        uid = f"Speaker-{self.persistent_id_counter:02d}"
        self.persistent_id_counter += 1
        return uid

    def _centroids(self, segment_registry):
        """Centroid per speaker from LOCKED segments only (dur >= 0.7s preferred)."""
        buckets = {}
        for seg in segment_registry:
            uid = seg.get('uuid')
            if not uid:
                continue
            dur = float(seg.get('end', 0.0) - seg.get('start', 0.0))
            # Prefer solid clips for centroid; still allow short if that is all we have
            buckets.setdefault(uid, []).append((dur, seg['embedding']))

        out = {}
        for uid, items in buckets.items():
            solid = [e for d, e in items if d >= 0.7]
            embs = solid if solid else [e for _, e in items]
            out[uid] = self._normalized_mean(embs)
        return out

    def _nearest(self, emb, centroids):
        best_uid, best_d = None, float('inf')
        second_uid, second_d = None, float('inf')
        for uid, cent in centroids.items():
            d = self._cos_dist(emb, cent)
            if d < best_d:
                second_uid, second_d = best_uid, best_d
                best_uid, best_d = uid, d
            elif d < second_d:
                second_uid, second_d = uid, d
        return best_uid, best_d, second_uid, second_d

    def _all_dists(self, emb, centroids):
        rows = [(uid, self._cos_dist(emb, cent)) for uid, cent in centroids.items()]
        rows.sort(key=lambda x: x[1])
        return rows

    def _log_compare(
        self,
        start,
        end,
        duration,
        dist_rows,
        best_uid,
        best_d,
        second_uid,
        second_d,
        last_uid,
        last_end,
        gap,
        d_prev_seg,
        can_mint,
        mint_block_reasons,
        decision_uid,
        decision_reason,
        n_spk,
        max_speakers,
    ):
        print(
            f"   [COMPARE] seg@{start:.2f}-{end:.2f}s  dur={duration:.2f}s  "
            f"n_spk={n_spk}/{max_speakers}  "
            f"thresh new≥{self.NEW_SPEAKER_MIN:.2f}  clear_margin≥{self.CLEAR_MARGIN:.2f}  "
            f"short<{self.SHORT_SEG_SEC:.1f}s",
            file=sys.stderr,
        )
        if dist_rows:
            parts = [f"{uid}={d:.3f}" for uid, d in dist_rows]
            print(f"   [COMPARE]   vs centroids: {', '.join(parts)}", file=sys.stderr)
            for uid, d in dist_rows:
                if d <= self.SAME_SPEAKER_MAX:
                    band = "SAME"
                elif d < self.NEW_SPEAKER_MIN:
                    band = "GRAY"
                else:
                    band = "FAR"
                print(
                    f"   [COMPARE]     {uid}: dist={d:.3f}  band={band}  "
                    f"(cos_sim={1.0 - d:.3f})",
                    file=sys.stderr,
                )
        else:
            print("   [COMPARE]   vs centroids: (none yet)", file=sys.stderr)

        margin = (second_d - best_d) if second_d < float('inf') else 0.0
        second_d_print = second_d if second_d < float('inf') else best_d
        print(
            f"   [COMPARE]   nearest={best_uid} best_d={best_d:.3f}  "
            f"second={second_uid} second_d={second_d_print:.3f}  "
            f"margin={margin:.3f}",
            file=sys.stderr,
        )
        if last_uid is not None:
            gap_s = f"{gap:.3f}s" if gap is not None else "?"
            dps = f"{d_prev_seg:.3f}" if d_prev_seg is not None else "n/a"
            print(
                f"   [COMPARE]   turn: last={last_uid} gap={gap_s}  "
                f"d_prev_segment={dps}",
                file=sys.stderr,
            )
        if can_mint:
            print("   [COMPARE]   can_mint=YES", file=sys.stderr)
        else:
            why = "; ".join(mint_block_reasons) if mint_block_reasons else "n/a"
            print(f"   [COMPARE]   can_mint=NO  ({why})", file=sys.stderr)
        print(
            f"   [COMPARE]   => {decision_uid}  ({decision_reason})",
            file=sys.stderr,
        )

    def process(self, segment_registry, expected_speakers, lc_gate_func=None):
        n_segments = len(segment_registry)
        if n_segments == 0:
            return {
                'segment_registry': segment_registry,
                'speaker_profiles_data': {},
                'persistent_id_counter': self.persistent_id_counter,
                'timeline_lines': [],
                'vis_nodes': [],
                'natural_variance': 0.1,
                'dynamic_threshold': 0.35,
                'uids': [],
                'c_dist': None,
            }

        max_speakers = expected_speakers if expected_speakers and expected_speakers > 0 else 6
        if expected_speakers == 1:
            max_speakers = 1
        min_speakers_floor = expected_speakers if expected_speakers and expected_speakers > 1 else 1

        # --- 1) Assign unlabeled segments only (locked segs never change) ---
        order = sorted(
            range(n_segments),
            key=lambda i: segment_registry[i].get('start', 0.0),
        )
        last_uid = None
        last_end = None
        last_emb = None

        # Seed last_* from already-locked segments in time order
        for i in order:
            seg = segment_registry[i]
            if seg.get('uuid'):
                last_uid = seg['uuid']
                last_end = seg.get('end', seg.get('start', 0.0))
                last_emb = self._norm_emb(seg['embedding'])

        for i in order:
            seg = segment_registry[i]
            if seg.get('uuid'):
                # LOCKED — do not touch
                last_uid = seg['uuid']
                last_end = seg.get('end', seg.get('start', 0.0))
                last_emb = self._norm_emb(seg['embedding'])
                continue

            emb = self._norm_emb(seg['embedding'])
            centroids = self._centroids(segment_registry)
            duration = float(seg.get('end', 0.0) - seg.get('start', 0.0))
            start = float(seg.get('start', 0.0))
            end = float(seg.get('end', start))
            gap = (start - float(last_end)) if last_end is not None else None
            d_prev_seg = (
                self._cos_dist(emb, last_emb) if last_emb is not None else None
            )

            if not centroids:
                uid = self._mint_speaker()
                seg['uuid'] = uid
                last_uid, last_end, last_emb = uid, end, emb
                print(f"   [STICKY] first speaker {uid}", file=sys.stderr)
                print(
                    f"   [COMPARE] seg@{start:.2f}-{end:.2f}s dur={duration:.2f}s  "
                    f"=> {uid} (first segment, no compare yet)",
                    file=sys.stderr,
                )
                continue

            best_uid, best_d, second_uid, second_d = self._nearest(emb, centroids)
            dist_rows = self._all_dists(emb, centroids)
            n_spk = len(centroids)
            margin = (second_d - best_d) if second_d < float('inf') else 0.0

            # --- Mint (only when room and embedding far from ALL centroids) ---
            mint_block_reasons = []
            if n_spk >= max_speakers:
                mint_block_reasons.append(f"at_cap n_spk={n_spk}>={max_speakers}")
            if duration < self.SHORT_SEG_SEC:
                mint_block_reasons.append(
                    f"short_seg dur={duration:.2f}<{self.SHORT_SEG_SEC:.1f}"
                )
            if best_d < self.NEW_SPEAKER_MIN:
                mint_block_reasons.append(
                    f"best_d={best_d:.3f}<NEW={self.NEW_SPEAKER_MIN:.2f}"
                )

            can_mint = (
                n_spk < max_speakers
                and duration >= self.SHORT_SEG_SEC
                and best_d >= self.NEW_SPEAKER_MIN
            )

            if can_mint:
                uid = self._mint_speaker()
                seg['uuid'] = uid
                reason = (
                    f"NEW nearest={best_uid} best_d={best_d:.3f}"
                    + (f" d_seg={d_prev_seg:.3f}" if d_prev_seg is not None else "")
                )
                print(f"   [STICKY NEW] {uid} ({reason})", file=sys.stderr)
            else:
                # --- Pure nearest; tie-break only when ambiguous ---
                if margin >= self.CLEAR_MARGIN or second_uid is None:
                    uid = best_uid
                    reason = (
                        f"nearest={uid} dist={best_d:.3f} margin={margin:.3f}"
                        + (f" d_seg={d_prev_seg:.3f}" if d_prev_seg is not None else "")
                    )
                else:
                    # Ambiguous: keep last speaker if they are competitive, else nearest
                    if last_uid is not None and last_uid in (best_uid, second_uid):
                        uid = last_uid
                        reason = (
                            f"ambiguous margin={margin:.3f}<{self.CLEAR_MARGIN:.2f} "
                            f"keep={uid} (best={best_uid}@{best_d:.3f} "
                            f"second={second_uid}@{second_d:.3f})"
                        )
                    else:
                        uid = best_uid
                        reason = (
                            f"ambiguous nearest={uid} dist={best_d:.3f} "
                            f"margin={margin:.3f}"
                        )
                seg['uuid'] = uid

            self._log_compare(
                start=start,
                end=end,
                duration=duration,
                dist_rows=dist_rows,
                best_uid=best_uid,
                best_d=best_d,
                second_uid=second_uid,
                second_d=second_d if second_d < float('inf') else best_d,
                last_uid=last_uid,
                last_end=last_end,
                gap=gap,
                d_prev_seg=d_prev_seg,
                can_mint=can_mint,
                mint_block_reasons=mint_block_reasons,
                decision_uid=seg['uuid'],
                decision_reason=reason,
                n_spk=n_spk,
                max_speakers=max_speakers,
            )

            last_uid = seg['uuid']
            last_end = end
            last_emb = emb

        # --- 2) NO consistency reassignment (was rewriting locked history) ---

        # --- 3) Speaker profiles ---
        profiles = {}
        for seg in segment_registry:
            uid = seg.get('uuid')
            if not uid:
                continue
            if uid not in profiles:
                profiles[uid] = {'embeddings': [], 'segments': []}
            profiles[uid]['embeddings'].append(seg['embedding'])
            profiles[uid]['segments'].append(seg)

        speaker_profiles_data = {}
        for uid, data in profiles.items():
            centroid = self._normalized_mean(data['embeddings'])
            best_dist = float('inf')
            best_audio = None
            for seg in data['segments']:
                dist = self._cos_dist(self._norm_emb(seg['embedding']), centroid)
                if dist < best_dist:
                    best_dist = dist
                    best_audio = seg.get('raw_audio')
            speaker_profiles_data[uid] = {
                'centroid': centroid,
                'best_audio': best_audio,
                'count': len(data['segments']),
            }

        intra_variances = []
        for uid, data in profiles.items():
            cent = speaker_profiles_data[uid]['centroid']
            dists = [
                self._cos_dist(self._norm_emb(e), cent) for e in data['embeddings']
            ]
            if len(dists) > 1:
                intra_variances.append(float(np.std(dists)))
        natural_variance = float(np.mean(intra_variances)) if intra_variances else 0.1
        dynamic_threshold = max(natural_variance * 2.5, 0.35)
        self.established_k = max(self.established_k, len(speaker_profiles_data))

        # --- 4) Singleton absorb: only noise, never below floor, never merge close real pair ---
        # Log showed Spk-01↔02 centroid dist ~0.16–0.28; absorb must stay BELOW that.
        SINGLETON_ABSORB_THRESH = 0.12
        SINGLETON_MAX_AGE = 6
        merged_any = False
        uids_snapshot = list(speaker_profiles_data.keys())
        for uid_s in uids_snapshot:
            if uid_s not in speaker_profiles_data:
                continue
            if speaker_profiles_data[uid_s]['count'] != 1:
                self._singleton_age.pop(uid_s, None)
                continue
            if len(speaker_profiles_data) <= min_speakers_floor:
                self._singleton_age[uid_s] = self._singleton_age.get(uid_s, 0) + 1
                continue

            current_age = self._singleton_age.get(uid_s, 0) + 1
            if current_age < SINGLETON_MAX_AGE:
                self._singleton_age[uid_s] = current_age
                continue

            best_host, best_host_dist = None, float('inf')
            for uid_h, pdata in speaker_profiles_data.items():
                if uid_h == uid_s or pdata['count'] <= 1:
                    continue
                d = self._cos_dist(
                    speaker_profiles_data[uid_s]['centroid'], pdata['centroid']
                )
                if d < best_host_dist:
                    best_host_dist = d
                    best_host = uid_h

            if best_host is not None and best_host_dist < SINGLETON_ABSORB_THRESH:
                print(
                    f"   [SINGLETON ABSORB] {uid_s} -> {best_host} "
                    f"(dist={best_host_dist:.3f} age={current_age})",
                    file=sys.stderr,
                )
                for seg in segment_registry:
                    if seg.get('uuid') == uid_s:
                        seg['uuid'] = best_host
                del speaker_profiles_data[uid_s]
                speaker_profiles_data[best_host]['count'] += 1
                self._singleton_age.pop(uid_s, None)
                merged_any = True
            else:
                self._singleton_age[uid_s] = current_age

        active_uids = set(speaker_profiles_data.keys())
        for uid in [u for u in self._singleton_age if u not in active_uids]:
            del self._singleton_age[uid]

        # --- 5) Post-merge ONLY near-identical centroids (<< real speaker pair) ---
        # Log closest real pair ~0.163; never merge near that.
        post_merge_thresh = 0.08
        uids = list(speaker_profiles_data.keys())
        for i in range(len(uids)):
            for j in range(i + 1, len(uids)):
                uid_a, uid_b = uids[i], uids[j]
                if uid_a not in speaker_profiles_data or uid_b not in speaker_profiles_data:
                    continue
                if len(speaker_profiles_data) <= min_speakers_floor:
                    break
                dist = self._cos_dist(
                    speaker_profiles_data[uid_a]['centroid'],
                    speaker_profiles_data[uid_b]['centroid'],
                )
                if dist < post_merge_thresh:
                    count_a = speaker_profiles_data[uid_a]['count']
                    count_b = speaker_profiles_data[uid_b]['count']
                    target, victim = (
                        (uid_a, uid_b) if count_a >= count_b else (uid_b, uid_a)
                    )
                    print(
                        f"[ACTIVE POST-MERGE] {victim} -> {target} "
                        f"(dist={dist:.3f} < {post_merge_thresh:.3f})",
                        file=sys.stderr,
                    )
                    for seg in segment_registry:
                        if seg.get('uuid') == victim:
                            seg['uuid'] = target
                    del speaker_profiles_data[victim]
                    merged_any = True

        if merged_any:
            profiles = {}
            for seg in segment_registry:
                uid = seg.get('uuid')
                if not uid:
                    continue
                profiles.setdefault(uid, {'embeddings': [], 'segments': []})
                profiles[uid]['embeddings'].append(seg['embedding'])
                profiles[uid]['segments'].append(seg)
            speaker_profiles_data = {}
            for uid, data in profiles.items():
                centroid = self._normalized_mean(data['embeddings'])
                best_dist, best_audio = float('inf'), None
                for seg in data['segments']:
                    dist = self._cos_dist(self._norm_emb(seg['embedding']), centroid)
                    if dist < best_dist:
                        best_dist = dist
                        best_audio = seg.get('raw_audio')
                speaker_profiles_data[uid] = {
                    'centroid': centroid,
                    'best_audio': best_audio,
                    'count': len(data['segments']),
                }

        uids = list(speaker_profiles_data.keys())
        timeline_lines = [
            (seg['start'], seg['end'], seg['uuid'])
            for seg in segment_registry
            if seg.get('uuid')
        ]

        vis_nodes = []
        c_dist = None
        if len(uids) > 1:
            try:
                c_dist = np.zeros((len(uids), len(uids)))
                for i in range(len(uids)):
                    for j in range(len(uids)):
                        c_dist[i, j] = self._cos_dist(
                            speaker_profiles_data[uids[i]]['centroid'],
                            speaker_profiles_data[uids[j]]['centroid'],
                        )
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
                        'x': float(pos[idx, 0]),
                        'y': float(pos[idx, 1]),
                        'count': speaker_profiles_data[uid]['count'],
                    })
            except Exception as e:
                print(f"MDS Error: {e}", file=sys.stderr)
                vis_nodes, c_dist = [], None
        elif len(uids) == 1:
            uid = uids[0]
            vis_nodes.append({
                'uid': uid, 'x': 0.5, 'y': 0.5,
                'count': speaker_profiles_data[uid]['count'],
            })
            c_dist = np.array([[0.0]])

        self._log_state(
            uids, speaker_profiles_data, c_dist, natural_variance, dynamic_threshold
        )

        return {
            'segment_registry': segment_registry,
            'speaker_profiles_data': speaker_profiles_data,
            'persistent_id_counter': self.persistent_id_counter,
            'timeline_lines': timeline_lines,
            'vis_nodes': vis_nodes,
            'natural_variance': natural_variance,
            'dynamic_threshold': dynamic_threshold,
            'uids': uids,
            'c_dist': c_dist,
        }

    def _log_state(self, uids, speaker_profiles_data, c_dist, natural_variance, dynamic_threshold):
        current_snapshot = {uid: speaker_profiles_data[uid]['count'] for uid in uids}
        prev = getattr(self, '_prev_snapshot', {})
        added = [uid for uid in current_snapshot if uid not in prev]
        removed = [uid for uid in prev if uid not in current_snapshot]
        changed = [
            uid for uid in current_snapshot
            if uid in prev and current_snapshot[uid] != prev[uid]
        ]
        has_structural_change = bool(added or removed)
        has_any_change = has_structural_change or bool(changed)
        self._log_tick = getattr(self, '_log_tick', 0) + 1
        should_print = has_structural_change or (has_any_change and self._log_tick % 5 == 0)
        if not should_print:
            self._prev_snapshot = current_snapshot
            return

        SEP = '-' * 56
        print(SEP, file=sys.stderr)
        if added:
            print(f"  [+] NEW SPEAKER(S): {', '.join(added)}", file=sys.stderr)
        if removed:
            print(f"  [-] MERGED/GONE:    {', '.join(removed)}", file=sys.stderr)
        print(f"  {'Speaker':<14} {'Segs':>5}  {'Intra-dist (to centroid)'}", file=sys.stderr)
        for uid in uids:
            seg_count = speaker_profiles_data[uid]['count']
            change_marker = ""
            if uid in added:
                change_marker = " <NEW>"
            elif uid in changed:
                delta = current_snapshot[uid] - prev.get(uid, 0)
                change_marker = f" (+{delta} segs)" if delta > 0 else f" ({delta} segs)"
            print(f"  {uid:<14} {seg_count:>5}  {change_marker}", file=sys.stderr)

        if c_dist is not None and len(uids) >= 2:
            col_w = 9
            header = " " * 16 + "".join(f"{uid:>{col_w}}" for uid in uids)
            print(
                f"\n  Distance Matrix  (merge_cap=0.08, dyn={dynamic_threshold:.3f})",
                file=sys.stderr,
            )
            print(f"  {header}", file=sys.stderr)
            for i, uid_row in enumerate(uids):
                row_vals = "".join(
                    f"{c_dist[i, j]:>{col_w}.3f}" for j in range(len(uids))
                )
                print(f"  {uid_row:<14}  {row_vals}", file=sys.stderr)
            min_dist = float('inf')
            min_pair = ("", "")
            for i in range(len(uids)):
                for j in range(i + 1, len(uids)):
                    if c_dist[i, j] < min_dist:
                        min_dist = c_dist[i, j]
                        min_pair = (uids[i], uids[j])
            risk = "RISK" if min_dist < 0.12 else "ok"
            print(
                f"\n  Closest pair: {min_pair[0]} <-> {min_pair[1]}  "
                f"dist={min_dist:.3f}  [{risk}]",
                file=sys.stderr,
            )
        elif len(uids) == 1:
            print("\n  [single speaker, no matrix]", file=sys.stderr)
        print(SEP, file=sys.stderr)
        self._prev_snapshot = current_snapshot
