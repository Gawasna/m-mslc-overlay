import sqlite3
import uuid
import datetime
from contextlib import closing
import os
import numpy as np
from typing import List, Tuple, Dict, Optional
import threading
from dataclasses import dataclass

MAX_POOL_SIZE          = 75     # max embeddings per profile — tăng từ 50 để chịu tải session dài hơn
COARSE_GATE            = 0.35   # centroid dist threshold — coarse filter
FINE_GATE              = 0.30   # pool 1-NN dist threshold — confirmation
MIN_VOTE_RATIO         = 0.25   # tỷ lệ tối thiểu pool phải vote đồng ý
MIN_VOTE_ABS           = 3      # sàn tuyệt đối để tránh pool nhỏ quá dễ
POOL_PURITY_THRESHOLD  = 0.15   # pool spread > này → siết coarse gate
MIN_CONFIRMED_SEGS     = 5      # min segments để profile tham gia recognition
TEMPORAL_SPREAD_MIN    = 60.0   # giây — thay MIN_SESSION_DIVERSITY=2

@dataclass
class ProfileCache:
    profile_id:   str
    display_name: str
    centroid:     np.ndarray          # (192,) L2-normalized
    pool:         List[np.ndarray]    # tối đa MAX_POOL_SIZE embeddings
    pool_meta:    List[dict]          # metadata cho mỗi embedding: {'session_id', 'start', 'end', 'captured_at'}
    pool_matrix:  Optional[np.ndarray] # (N, 192) pre-stacked, None nếu chưa build
    segment_count: int
    dirty:        bool                # True nếu có thay đổi chưa flush vào DB
    is_user_confirmed: bool           # True nếu user đã label hoặc confirm
    temporal_spread: float            # giây

class VoiceProfileDB:
    # Cross-session recognition threshold.
    CROSS_SESSION_RECOGNITION_THRESH = 0.30

    def __init__(self, db_path: str):
        self.db_path = db_path
        self._cache: Dict[str, ProfileCache] = {}
        self._lock = threading.Lock()
        self._init_db()
        self._warm_cache()

    def _init_db(self):
        if self.db_path != ':memory:':
            os.makedirs(os.path.dirname(os.path.abspath(self.db_path)), exist_ok=True)
        with closing(sqlite3.connect(self.db_path)) as conn:
            conn.execute('PRAGMA journal_mode=WAL')
            cursor = conn.cursor()
            cursor.execute('''
                CREATE TABLE IF NOT EXISTS speaker_profiles (
                    profile_id    TEXT PRIMARY KEY,
                    display_name  TEXT NOT NULL DEFAULT '',
                    centroid_blob BLOB NOT NULL,
                    created_at    TEXT NOT NULL,
                    last_seen_at  TEXT NOT NULL,
                    session_count INTEGER NOT NULL DEFAULT 1,
                    segment_count INTEGER NOT NULL DEFAULT 0,
                    is_active     INTEGER NOT NULL DEFAULT 1,
                    is_user_confirmed INTEGER NOT NULL DEFAULT 0
                )
            ''')
            cursor.execute('''
                CREATE TABLE IF NOT EXISTS speaker_metadata (
                    profile_id  TEXT NOT NULL REFERENCES speaker_profiles(profile_id),
                    key         TEXT NOT NULL,
                    value       TEXT NOT NULL,
                    updated_at  TEXT NOT NULL,
                    PRIMARY KEY (profile_id, key)
                )
            ''')
            cursor.execute('''
                CREATE TABLE IF NOT EXISTS speaker_embeddings (
                    id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    profile_id   TEXT NOT NULL REFERENCES speaker_profiles(profile_id),
                    embedding    BLOB NOT NULL,
                    dist_to_centroid REAL NOT NULL,
                    captured_at  TEXT NOT NULL
                )
            ''')
            cursor.execute('CREATE INDEX IF NOT EXISTS idx_embeddings_profile ON speaker_embeddings(profile_id)')
            cursor.execute('''
                CREATE TABLE IF NOT EXISTS recognition_log (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    profile_id  TEXT NOT NULL,
                    session_id  TEXT NOT NULL,
                    action      TEXT NOT NULL,
                    dist        REAL,
                    timestamp   TEXT NOT NULL
                )
            ''')
            cursor.execute('''
                CREATE TABLE IF NOT EXISTS merge_log (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    source_id       TEXT NOT NULL,
                    target_id       TEXT NOT NULL,
                    source_centroid BLOB NOT NULL,
                    target_centroid BLOB NOT NULL,
                    source_seg_count INTEGER NOT NULL,
                    merged_at       TEXT NOT NULL,
                    session_id      TEXT NOT NULL,
                    undone          INTEGER NOT NULL DEFAULT 0
                )
            ''')
            cursor.execute('''
                CREATE TABLE IF NOT EXISTS dismissed_pairs (
                    profile_a TEXT NOT NULL,
                    profile_b TEXT NOT NULL,
                    dismissed_at TEXT NOT NULL,
                    PRIMARY KEY (profile_a, profile_b)
                )
            ''')
            try:
                cursor.execute('ALTER TABLE speaker_embeddings ADD COLUMN session_id TEXT')
            except sqlite3.OperationalError:
                pass
            try:
                cursor.execute('ALTER TABLE speaker_embeddings ADD COLUMN segment_start_sec REAL')
            except sqlite3.OperationalError:
                pass
            try:
                cursor.execute('ALTER TABLE speaker_embeddings ADD COLUMN segment_end_sec REAL')
            except sqlite3.OperationalError:
                pass
            try:
                cursor.execute('ALTER TABLE speaker_profiles ADD COLUMN is_user_confirmed INTEGER NOT NULL DEFAULT 0')
            except sqlite3.OperationalError:
                pass
            
            conn.commit()

    def _now(self):
        return datetime.datetime.now(datetime.timezone.utc).isoformat()

    def _warm_cache(self):
        with closing(sqlite3.connect(self.db_path)) as conn:
            cursor = conn.cursor()
            
            cursor.execute('SELECT profile_id, display_name, centroid_blob, segment_count, is_user_confirmed FROM speaker_profiles WHERE is_active = 1')
            profiles_rows = cursor.fetchall()
            
            for row in profiles_rows:
                pid, name, blob, seg_count, is_confirmed = row
                centroid = np.frombuffer(blob, dtype=np.float32)
                
                cursor.execute('SELECT embedding, captured_at, session_id, segment_start_sec, segment_end_sec FROM speaker_embeddings WHERE profile_id = ?', (pid,))
                emb_rows = cursor.fetchall()
                
                pool = []
                pool_meta = []
                for erow in emb_rows:
                    pool.append(np.frombuffer(erow[0], dtype=np.float32))
                    pool_meta.append({
                        'captured_at': erow[1],
                        'session_id': erow[2] if erow[2] is not None else '',
                        'start': erow[3] if erow[3] is not None else 0.0,
                        'end': erow[4] if erow[4] is not None else 0.0,
                        'flushed': True
                    })
                
                pool_matrix = np.stack(pool) if pool else None
                
                starts = [m['start'] for m in pool_meta if m['start'] > 0]
                ends   = [m['end']   for m in pool_meta if m['end']   > 0]
                temporal_spread = (max(ends) - min(starts)) if starts and ends else 0.0
                
                self._cache[pid] = ProfileCache(
                    profile_id=pid,
                    display_name=name,
                    centroid=centroid,
                    pool=pool,
                    pool_meta=pool_meta,
                    pool_matrix=pool_matrix,
                    segment_count=seg_count,
                    dirty=False,
                    is_user_confirmed=bool(is_confirmed),
                    temporal_spread=temporal_spread
                )

    def _recompute_centroid(self, profile_id: str, conn=None) -> np.ndarray:
        pass

    def add_embedding(self, profile_id: str, embedding: np.ndarray, conn=None, session_id: str = '', start_sec: float = 0.0, end_sec: float = 0.0) -> None:
        embedding = embedding.astype(np.float32)
        embedding = embedding / np.linalg.norm(embedding)

        with self._lock:
            cache = self._cache.get(profile_id)
            if not cache:
                return

            if len(cache.pool) >= MAX_POOL_SIZE:
                dists = [1.0 - float(np.dot(e, cache.centroid)) for e in cache.pool]
                worst_idx = int(np.argmax(dists))
                cache.pool.pop(worst_idx)
                cache.pool_meta.pop(worst_idx)
                cache.needs_full_flush = True

            cache.pool.append(embedding)
            cache.pool_meta.append({
                'captured_at': self._now(),
                'session_id': session_id,
                'start': start_sec,
                'end': end_sec,
                'flushed': False
            })
            cache.segment_count += 1
            
            starts = [m['start'] for m in cache.pool_meta if m['start'] > 0]
            ends   = [m['end']   for m in cache.pool_meta if m['end']   > 0]
            cache.temporal_spread = (max(ends) - min(starts)) if starts and ends else 0.0

            pool_arr = np.stack(cache.pool)
            mean = np.mean(pool_arr, axis=0)
            cache.centroid = mean / np.linalg.norm(mean)

            cache.pool_matrix = pool_arr.copy()
            cache.dirty = True

    def flush_to_db(self, session_id: str) -> None:
        now = self._now()
        with closing(sqlite3.connect(self.db_path)) as conn:
            conn.execute('PRAGMA journal_mode=WAL')
            cursor = conn.cursor()

            for pid, cache in self._cache.items():
                if not cache.dirty:
                    continue

                cursor.execute('''
                    UPDATE speaker_profiles
                    SET centroid_blob=?, segment_count=?, last_seen_at=?
                    WHERE profile_id=?
                ''', (cache.centroid.tobytes(), cache.segment_count, now, pid))

                needs_full = getattr(cache, 'needs_full_flush', False)
                if needs_full:
                    cursor.execute('DELETE FROM speaker_embeddings WHERE profile_id=?', (pid,))
                    pending = [
                        (pid, e.tobytes(), float(1.0 - np.dot(e, cache.centroid)), meta['captured_at'], meta['session_id'], meta['start'], meta['end'])
                        for e, meta in zip(cache.pool, cache.pool_meta)
                    ]
                    cache.needs_full_flush = False
                else:
                    pending = [
                        (pid, e.tobytes(), float(1.0 - np.dot(e, cache.centroid)), meta['captured_at'], meta['session_id'], meta['start'], meta['end'])
                        for e, meta in zip(cache.pool, cache.pool_meta)
                        if not meta.get('flushed', False)
                    ]
                
                if pending:
                    cursor.executemany('''
                        INSERT INTO speaker_embeddings (profile_id, embedding, dist_to_centroid, captured_at, session_id, segment_start_sec, segment_end_sec)
                        VALUES (?, ?, ?, ?, ?, ?, ?)
                    ''', pending)
                    for meta in cache.pool_meta:
                        meta['flushed'] = True

                cache.dirty = False

            conn.commit()

    def create_profile(self, centroid: np.ndarray,
                       initial_embeddings: List[dict] = None,
                       display_name: str = '') -> str:
        profile_id = str(uuid.uuid4())
        centroid = centroid.astype(np.float32)
        centroid = centroid / np.linalg.norm(centroid)
        pool = []
        pool_meta = []

        now = self._now()
        if initial_embeddings:
            for item in initial_embeddings[:MAX_POOL_SIZE]:
                e = item['embedding'].astype(np.float32)
                e = e / np.linalg.norm(e)
                pool.append(e)
                pool_meta.append({
                    'captured_at': now,
                    'session_id': item.get('session_id', ''),
                    'start': item.get('start', 0.0),
                    'end': item.get('end', 0.0),
                    'flushed': True
                })
            if pool:
                arr = np.stack(pool)
                mean = np.mean(arr, axis=0)
                centroid = mean / np.linalg.norm(mean)

        with closing(sqlite3.connect(self.db_path)) as conn:
            conn.execute('PRAGMA journal_mode=WAL')
            cursor = conn.cursor()
            cursor.execute('''
                INSERT INTO speaker_profiles
                (profile_id, display_name, centroid_blob, created_at, last_seen_at, session_count, segment_count, is_active)
                VALUES (?, ?, ?, ?, ?, 1, ?, 1)
            ''', (profile_id, display_name, centroid.tobytes(), now, now, len(pool)))
            
            cursor.execute('''
                INSERT INTO recognition_log (profile_id, session_id, action, dist, timestamp)
                VALUES (?, ?, ?, ?, ?)
            ''', (profile_id, 'SYSTEM_INIT', 'created', 0.0, now))
            
            if pool:
                cursor.executemany('''
                    INSERT INTO speaker_embeddings (profile_id, embedding, dist_to_centroid, captured_at, session_id, segment_start_sec, segment_end_sec)
                    VALUES (?, ?, ?, ?, ?, ?, ?)
                ''', [(profile_id, e.tobytes(), float(1.0 - np.dot(e, centroid)), meta['captured_at'], meta['session_id'], meta['start'], meta['end']) for e, meta in zip(pool, pool_meta)])
            
            conn.commit()

        pm = np.stack(pool) if pool else None
        starts = [m['start'] for m in pool_meta if m['start'] > 0]
        ends   = [m['end']   for m in pool_meta if m['end']   > 0]
        temporal_spread = (max(ends) - min(starts)) if starts and ends else 0.0

        with self._lock:
            self._cache[profile_id] = ProfileCache(
                profile_id=profile_id,
                display_name=display_name,
                centroid=centroid,
                pool=pool,
                pool_meta=pool_meta,
                pool_matrix=pm,
                segment_count=len(pool),
                dirty=False,
                is_user_confirmed=False,
                temporal_spread=temporal_spread
            )
        return profile_id

    def set_display_name(self, profile_id: str, name: str) -> None:
        with closing(sqlite3.connect(self.db_path)) as conn:
            conn.execute('PRAGMA journal_mode=WAL')
            cursor = conn.cursor()
            cursor.execute('UPDATE speaker_profiles SET display_name = ?, is_user_confirmed = 1 WHERE profile_id = ?', (name, profile_id))
            conn.commit()
        with self._lock:
            if profile_id in self._cache:
                self._cache[profile_id].display_name = name
                self._cache[profile_id].is_user_confirmed = True

    def set_user_confirmed(self, profile_id: str, confirmed: bool = True) -> None:
        val = 1 if confirmed else 0
        with closing(sqlite3.connect(self.db_path)) as conn:
            conn.execute('PRAGMA journal_mode=WAL')
            cursor = conn.cursor()
            cursor.execute('UPDATE speaker_profiles SET is_user_confirmed = ? WHERE profile_id = ?', (val, profile_id))
            conn.commit()
        with self._lock:
            if profile_id in self._cache:
                self._cache[profile_id].is_user_confirmed = confirmed

    def set_metadata(self, profile_id: str, key: str, value: str) -> None:
        now = self._now()
        with closing(sqlite3.connect(self.db_path)) as conn:
            conn.execute('PRAGMA journal_mode=WAL')
            cursor = conn.cursor()
            cursor.execute('''
                INSERT INTO speaker_metadata (profile_id, key, value, updated_at) 
                VALUES (?, ?, ?, ?)
                ON CONFLICT(profile_id, key) DO UPDATE SET value=excluded.value, updated_at=excluded.updated_at
            ''', (profile_id, key, value, now))
            conn.commit()

    def get_metadata(self, profile_id: str, key: str) -> str:
        with closing(sqlite3.connect(self.db_path)) as conn:
            cursor = conn.cursor()
            cursor.execute('SELECT value FROM speaker_metadata WHERE profile_id = ? AND key = ?', (profile_id, key))
            row = cursor.fetchone()
            return row[0] if row else ""

    def soft_delete(self, profile_id: str) -> None:
        with closing(sqlite3.connect(self.db_path)) as conn:
            conn.execute('PRAGMA journal_mode=WAL')
            cursor = conn.cursor()
            cursor.execute('UPDATE speaker_profiles SET is_active = 0 WHERE profile_id = ?', (profile_id,))
            conn.commit()
        with self._lock:
            if profile_id in self._cache:
                del self._cache[profile_id]

    def load_all_active(self) -> list:
        profiles = []
        with self._lock:
            for cache in self._cache.values():
                profiles.append({
                    'profile_id': cache.profile_id,
                    'display_name': cache.display_name,
                    'centroid': cache.centroid.copy(),
                    'segment_count': cache.segment_count
                })
        return profiles

    def _load_pool(self, profile_id: str) -> List[np.ndarray]:
        with self._lock:
            if profile_id in self._cache:
                return self._cache[profile_id].pool.copy()
        return []

    def recognize(self, query: np.ndarray,
                  coarse_thresh: float = COARSE_GATE,
                  fine_thresh: float = FINE_GATE) -> Tuple[Optional[str], float]:
        query = query.astype(np.float32)
        query = query / np.linalg.norm(query)

        candidates = []
        with self._lock:
            for pid, cache in self._cache.items():
                if not cache.is_user_confirmed and not cache.display_name:
                    continue
                
                if cache.segment_count < MIN_CONFIRMED_SEGS:
                    continue

                pool_spread = 0.0
                if cache.pool_matrix is not None and len(cache.pool) >= 5:
                    dists_intra = 1.0 - (cache.pool_matrix @ cache.centroid)
                    pool_spread = float(np.mean(dists_intra))

                effective_coarse = coarse_thresh * 0.7 if pool_spread > POOL_PURITY_THRESHOLD else coarse_thresh
                
                centroid_dist = 1.0 - float(np.dot(query, cache.centroid))
                if centroid_dist < effective_coarse:
                    candidates.append((pid, centroid_dist, cache))

        if not candidates:
            return None, 1.0

        best_pid, best_dist = None, float(fine_thresh)
        for pid, centroid_dist, cache in candidates:
            if cache.pool_matrix is None or len(cache.pool) == 0:
                if centroid_dist < best_dist:
                    best_dist, best_pid = centroid_dist, pid
                continue

            dists = 1.0 - (cache.pool_matrix @ query)
            
            required_votes = max(MIN_VOTE_ABS, int(MIN_VOTE_RATIO * len(cache.pool)))
            votes = int(np.sum(dists < fine_thresh))
            
            if votes < required_votes:
                continue
                
            matching_sessions = {cache.pool_meta[i]['session_id'] for i in range(len(dists)) if dists[i] < fine_thresh}
            is_diverse = (
                len(matching_sessions) >= 2
                or cache.temporal_spread >= TEMPORAL_SPREAD_MIN
            )
            
            if not is_diverse:
                continue

            min_dist = float(np.min(dists))
            if min_dist < best_dist:
                best_dist, best_pid = min_dist, pid

        return best_pid, best_dist

    def recognize_from_pool(
        self,
        query_embeddings: List[np.ndarray],
        consistency_ratio: float = 0.50
    ) -> Tuple[Optional[str], float, float]:
        if not query_embeddings:
            return None, 1.0, 0.0

        vote_table: Dict[str, int] = {}
        dist_table: Dict[str, float] = {}
        
        for emb in query_embeddings:
            pid, dist = self.recognize(emb)
            if pid is not None:
                vote_table[pid] = vote_table.get(pid, 0) + 1
                dist_table[pid] = min(dist_table.get(pid, 1.0), dist)

        if not vote_table:
            return None, 1.0, 0.0

        winner_pid = max(vote_table, key=lambda x: vote_table[x])
        winner_votes = vote_table[winner_pid]
        consistency = winner_votes / len(query_embeddings)
        
        if consistency >= consistency_ratio:
            return winner_pid, dist_table[winner_pid], consistency
        else:
            return None, dist_table.get(winner_pid, 1.0), consistency

    def _remove_embeddings_by_timestamp(self, profile_id: str, start_sec: float, end_sec: float, tolerance: float = 0.5):
        with self._lock:
            cache = self._cache.get(profile_id)
            if not cache: return
            
            new_pool = []
            new_meta = []
            for emb, meta in zip(cache.pool, cache.pool_meta):
                if abs(meta['start'] - start_sec) < tolerance and abs(meta['end'] - end_sec) < tolerance:
                    continue
                new_pool.append(emb)
                new_meta.append(meta)
            
            cache.pool = new_pool
            cache.pool_meta = new_meta
            cache.segment_count = len(new_pool)
            
            starts = [m['start'] for m in cache.pool_meta if m['start'] > 0]
            ends   = [m['end']   for m in cache.pool_meta if m['end']   > 0]
            cache.temporal_spread = (max(ends) - min(starts)) if starts and ends else 0.0
            
            if new_pool:
                arr = np.stack(new_pool)
                mean = np.mean(arr, axis=0)
                cache.centroid = mean / np.linalg.norm(mean)
                cache.pool_matrix = arr.copy()
            else:
                cache.pool_matrix = None
            cache.dirty = True

    def get_candidates(self, centroid: np.ndarray, top_k: int = 5) -> list:
        centroid = centroid.astype(np.float32)
        centroid = centroid / np.linalg.norm(centroid)
        
        results = []
        with self._lock:
            for p in self._cache.values():
                if p.pool_matrix is not None and len(p.pool) > 0:
                    sims = p.pool_matrix @ centroid
                    dist = float(1.0 - np.max(sims))
                else:
                    dist = float(1.0 - np.dot(centroid, p.centroid))
                results.append((p.profile_id, dist, p.display_name))
            
        results.sort(key=lambda x: x[1])
        return results[:top_k]

    def get_merge_suggestions(self, coarse_gate: float = COARSE_GATE) -> list:
        suggestions = []
        with self._lock:
            pids = list(self._cache.keys())
            
            dismissed = set()
            with closing(sqlite3.connect(self.db_path)) as conn:
                cursor = conn.cursor()
                cursor.execute('SELECT profile_a, profile_b FROM dismissed_pairs')
                for a, b in cursor.fetchall():
                    dismissed.add((a, b))
                    dismissed.add((b, a))
            
            for i in range(len(pids)):
                for j in range(i + 1, len(pids)):
                    pid1, pid2 = pids[i], pids[j]
                    if (pid1, pid2) in dismissed:
                        continue
                    
                    c1 = self._cache[pid1]
                    c2 = self._cache[pid2]
                    
                    dist = 1.0 - float(np.dot(c1.centroid, c2.centroid))
                    if dist < coarse_gate:
                        name1 = c1.display_name or pid1[:8]
                        name2 = c2.display_name or pid2[:8]
                        suggestions.append((pid1, name1, pid2, name2, dist))
        
        suggestions.sort(key=lambda x: x[4])
        return suggestions

    def dismiss_merge_suggestion(self, pid1: str, pid2: str) -> None:
        now = self._now()
        with closing(sqlite3.connect(self.db_path)) as conn:
            conn.execute('PRAGMA journal_mode=WAL')
            cursor = conn.cursor()
            cursor.execute('INSERT OR IGNORE INTO dismissed_pairs (profile_a, profile_b, dismissed_at) VALUES (?, ?, ?)', (pid1, pid2, now))
            conn.commit()

    def merge_profiles(self, source_id: str, target_id: str, session_id: str = "MANUAL_MERGE") -> None:
        with closing(sqlite3.connect(self.db_path)) as conn:
            conn.execute('PRAGMA journal_mode=WAL')
            cursor = conn.cursor()
            
            cursor.execute('SELECT centroid_blob, segment_count, session_count FROM speaker_profiles WHERE profile_id = ?', (source_id,))
            source_row = cursor.fetchone()
            
            cursor.execute('SELECT centroid_blob, segment_count, session_count FROM speaker_profiles WHERE profile_id = ?', (target_id,))
            target_row = cursor.fetchone()
            
            if not source_row or not target_row:
                raise ValueError("Source or target profile not found")
                
            source_blob, source_seg, source_sess = source_row
            target_blob, target_seg, target_sess = target_row
            
            now = self._now()
            cursor.execute('''
                INSERT INTO merge_log (source_id, target_id, source_centroid, target_centroid, source_seg_count, merged_at, session_id)
                VALUES (?, ?, ?, ?, ?, ?, ?)
            ''', (source_id, target_id, source_blob, target_blob, source_seg, now, session_id))
            
            cursor.execute('UPDATE speaker_embeddings SET profile_id = ? WHERE profile_id = ?', (target_id, source_id))
            
            cursor.execute('SELECT embedding, captured_at, session_id, segment_start_sec, segment_end_sec FROM speaker_embeddings WHERE profile_id = ?', (target_id,))
            embs = cursor.fetchall()
            pool = []
            pool_meta = []
            for erow in embs:
                pool.append(np.frombuffer(erow[0], dtype=np.float32))
                pool_meta.append({
                    'captured_at': erow[1],
                    'session_id': erow[2] if erow[2] is not None else '',
                    'start': erow[3] if erow[3] is not None else 0.0,
                    'end': erow[4] if erow[4] is not None else 0.0
                })
            
            if len(pool) > MAX_POOL_SIZE:
                mean_approx = np.mean(np.stack(pool), axis=0)
                mean_approx = mean_approx / np.linalg.norm(mean_approx)
                dists = [1.0 - float(np.dot(e, mean_approx)) for e in pool]
                sorted_idx = np.argsort(dists)
                
                best_pool = [pool[i] for i in sorted_idx[:MAX_POOL_SIZE]]
                best_pool_meta = [pool_meta[i] for i in sorted_idx[:MAX_POOL_SIZE]]
                
                cursor.execute('DELETE FROM speaker_embeddings WHERE profile_id = ?', (target_id,))
                
                mean_actual = np.mean(np.stack(best_pool), axis=0)
                mean_actual = mean_actual / np.linalg.norm(mean_actual)
                
                cursor.executemany('''
                    INSERT INTO speaker_embeddings (profile_id, embedding, dist_to_centroid, captured_at, session_id, segment_start_sec, segment_end_sec)
                    VALUES (?, ?, ?, ?, ?, ?, ?)
                ''', [(target_id, e.tobytes(), float(1.0 - np.dot(e, mean_actual)), meta['captured_at'], meta['session_id'], meta['start'], meta['end']) for e, meta in zip(best_pool, best_pool_meta)])
                pool = best_pool
                pool_meta = best_pool_meta
            else:
                mean_actual = np.mean(np.stack(pool), axis=0)
                mean_actual = mean_actual / np.linalg.norm(mean_actual)

            total_seg = source_seg + target_seg
            new_sess = target_sess + source_sess
            
            cursor.execute('''
                UPDATE speaker_profiles 
                SET centroid_blob = ?, segment_count = ?, session_count = ?, last_seen_at = ?
                WHERE profile_id = ?
            ''', (mean_actual.tobytes(), total_seg, new_sess, now, target_id))
            
            cursor.execute('UPDATE speaker_profiles SET is_active = 0 WHERE profile_id = ?', (source_id,))
            
            cursor.execute('''
                INSERT INTO recognition_log (profile_id, session_id, action, dist, timestamp)
                VALUES (?, ?, ?, ?, ?)
            ''', (target_id, session_id, 'merged', 0.0, now))
            
            conn.commit()
            
        with self._lock:
            if source_id in self._cache:
                del self._cache[source_id]
                
            pm = np.stack(pool) if pool else None
            starts = [m['start'] for m in pool_meta if m['start'] > 0]
            ends   = [m['end']   for m in pool_meta if m['end']   > 0]
            temporal_spread = (max(ends) - min(starts)) if starts and ends else 0.0

            self._cache[target_id] = ProfileCache(
                profile_id=target_id,
                display_name=self.get_metadata(target_id, 'display_name'),
                centroid=mean_actual,
                pool=pool,
                pool_meta=pool_meta,
                pool_matrix=pm,
                segment_count=total_seg,
                dirty=False,
                is_user_confirmed=self._cache[target_id].is_user_confirmed if target_id in self._cache else False,
                temporal_spread=temporal_spread
            )

    def reassign_segment(self, session_id: str, start_sec: float, end_sec: float, old_pid: str, new_pid: str) -> None:
        with closing(sqlite3.connect(self.db_path)) as conn:
            conn.execute('PRAGMA journal_mode=WAL')
            cursor = conn.cursor()
            
            # Find matching embeddings in DB
            # We use ABS(segment_start_sec - start_sec) < 0.1 to account for floating point differences
            cursor.execute('''
                UPDATE speaker_embeddings 
                SET profile_id = ? 
                WHERE profile_id = ? AND session_id = ? 
                  AND ABS(segment_start_sec - ?) < 0.1 
                  AND ABS(segment_end_sec - ?) < 0.1
            ''', (new_pid, old_pid, session_id, start_sec, end_sec))
            
            conn.commit()
            
            # We updated DB directly. The easiest way to refresh memory is _warm_cache for just these 2 profiles
            for pid in [old_pid, new_pid]:
                cursor.execute('SELECT profile_id, display_name, centroid_blob, segment_count FROM speaker_profiles WHERE profile_id = ? AND is_active = 1', (pid,))
                row = cursor.fetchone()
                if row:
                    _, name, blob, seg_count = row
                    centroid = np.frombuffer(blob, dtype=np.float32)
                    
                    cursor.execute('SELECT embedding, captured_at, session_id, segment_start_sec, segment_end_sec FROM speaker_embeddings WHERE profile_id = ?', (pid,))
                    emb_rows = cursor.fetchall()
                    
                    pool = []
                    pool_meta = []
                    for erow in emb_rows:
                        pool.append(np.frombuffer(erow[0], dtype=np.float32))
                        pool_meta.append({
                            'captured_at': erow[1],
                            'session_id': erow[2] if erow[2] is not None else '',
                            'start': erow[3] if erow[3] is not None else 0.0,
                            'end': erow[4] if erow[4] is not None else 0.0
                        })
                    
                    # Recompute centroid based on new pool and update DB centroid
                    if pool:
                        pm = np.stack(pool)
                        new_centroid = np.mean(pm, axis=0)
                        new_centroid = new_centroid / np.linalg.norm(new_centroid)
                        cursor.execute('UPDATE speaker_profiles SET centroid_blob = ?, segment_count = ? WHERE profile_id = ?', (new_centroid.tobytes(), len(pool), pid))
                        conn.commit()
                        centroid = new_centroid
                        seg_count = len(pool)
                    else:
                        pm = None
                        
                    with self._lock:
                        self._cache[pid] = ProfileCache(
                            profile_id=pid,
                            display_name=name,
                            centroid=centroid,
                            pool=pool,
                            pool_meta=pool_meta,
                            pool_matrix=pm,
                            segment_count=seg_count,
                            dirty=False,
                            is_user_confirmed=self._cache[pid].is_user_confirmed if pid in self._cache else False
                        )
            
            cursor.execute('''
                INSERT INTO recognition_log (profile_id, session_id, action, dist, timestamp)
                VALUES (?, ?, ?, ?, ?)
            ''', (new_pid, session_id, 'reassigned_from_' + old_pid, 0.0, self._now()))
            conn.commit()
