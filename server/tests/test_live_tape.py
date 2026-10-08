"""Offline regressions for resumable tape collection; never contact providers."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('quoteview_server', Path(__file__).parents[1] / 'scripts/server.py')
s = importlib.util.module_from_spec(spec)
spec.loader.exec_module(s)


def row(seq, when=None):
    when = when or f'10:{seq // 20:02}:{seq % 20 * 3:02}'
    return f'{seq}/{when}/10.05/0/10/10050/B'


class Response:
    def __init__(self, body): self.body = body
    def __enter__(self): return self
    def __exit__(self, *args): pass
    def read(self): return self.body.encode()


class TapeTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        for name, value in [('LIVE_STORE', self.tmp.name + '/live'), ('TICKS', self.tmp.name + '/ticks'),
                            ('_live_day', lambda: '2026-10-08'), ('_live_after_close', lambda: False),
                            ('_snapshot_stale', lambda rows: False), ('log', lambda text: None)]:
            p = patch.object(s, name, value); p.start(); self.addCleanup(p.stop)
        s.LIVE.clear(); s.LIVE_WANT.clear(); s._seed_active.clear()
        self.code = 'SZ001258'
        self.pages = []

    def entry(self): return s._live_entry(self.code)

    def fetch(self, pages, tail, start=0):
        def request(req, timeout):
            page = int(req.full_url.rsplit('=', 1)[1]); self.pages.append(page)
            value = pages[page]
            if isinstance(value, list): value = value.pop(0)
            if isinstance(value, Exception): raise value
            return Response(value)
        with patch.object(s, 'fetch_ticks_tencent', return_value=tail), \
             patch.object(s.urllib.request, 'urlopen', side_effect=request), \
             patch.object(s.time, 'sleep'):
            return s.fetch_ticks_tencent_fullday(self.code, start)

    def page(self, page, ids):
        return f'v_detail_data_sz001258=[{page},"' + '|'.join(row(i) for i in ids) + '"]'

    def test_page_zero_and_exact_tail(self):
        result = self.fetch({0:self.page(0, range(70)), 1:self.page(1, range(70, 92))}, [(91, '10:04:33,10.05,10,0,2')])
        self.assertEqual(self.pages, [0, 1]); self.assertEqual(len(result['Details']), 92)
        self.assertEqual(result['next_seq'], 92); self.assertTrue(result['complete'])

    def test_empty_page_retried_without_skipping(self):
        result = self.fetch({0:['', self.page(0, range(70))], 1:self.page(1, range(70, 80))}, [(79, '10:03:57,10,1,0,2')])
        self.assertEqual(self.pages, [0, 0, 1]); self.assertEqual(result['next_seq'], 80)

    def test_discontinuous_page_retried(self):
        result = self.fetch({0:[self.page(0, [0, 2]), self.page(0, range(3))]}, [(2, '10:00:06,10,1,0,2')])
        self.assertTrue(result['complete']); self.assertEqual(self.pages, [0, 0])

    def test_failure_returns_cursor_then_resumes(self):
        r = self.fetch({0:self.page(0, range(70)), 1:''}, [(139, '10:06:57,10,1,0,2')])
        self.assertFalse(r['complete']); self.assertEqual(r['next_seq'], 70)
        self.pages.clear()
        r = self.fetch({1:self.page(1, range(70, 140))}, [(139, '10:06:57,10,1,0,2')], start=70)
        self.assertTrue(r['complete']); self.assertEqual(self.pages, [1])

    def test_unavailable_tail_does_not_skip_history(self):
        r = self.fetch({0:self.page(0, range(10))}, None)
        self.assertEqual(r['next_seq'], 10); self.assertFalse(r['complete'])

    def test_partial_page_is_revisited(self):
        r = self.fetch({1:self.page(1, range(70, 95))}, [(94, '10:04:42,10,1,0,2')], start=80)
        self.assertEqual(len(r['Details']), 15); self.assertEqual(r['next_seq'], 95)
        self.assertEqual(self.pages, [1])

    def test_batch_budget_preserves_progress(self):
        with patch.object(s, 'LIVE_PAGE_BUDGET', 1):
            r = self.fetch({0:self.page(0, range(70))}, [(140, '10:07:00,10,1,0,2')])
        self.assertEqual(r['next_seq'], 70); self.assertFalse(r['complete'])

    def test_live_tail_bridges_lagging_final_history_page(self):
        tail=[(i, f'15:28:{i - 3850:02},10,1,0,2') for i in range(3836,3856)]
        r=self.fetch({},tail,start=3851)
        self.assertEqual(r['next_seq'],3856); self.assertTrue(r['complete'])
        self.assertEqual(len(r['Details']),5); self.assertEqual(self.pages,[])

    def test_live_tail_must_not_jump_over_missing_sequence(self):
        tail=[(i,'14:00:00,10,1,0,2') for i in range(82,90)]
        r=self.fetch({1:''},tail,start=80)
        self.assertEqual(r['next_seq'],80); self.assertFalse(r['complete'])

    def test_idle_resume_requests_repair_without_clearing(self):
        e = self.entry(); e.update(next_seq=100, check_at=99999999999)
        e['rows']['13:52:24'] = '13:52:24,10,1,0,2'
        s.LIVE_WANT[self.code] = 1
        s.live_subscribe(self.code)
        self.assertEqual(e['next_seq'], 100); self.assertEqual(e['check_at'], 0)
        _, jobs = s._live_schedule(s.time.time())
        self.assertEqual(len(jobs), 1); self.assertEqual(len(e['rows']), 1)

    def test_no_duplicate_or_unbounded_seed_queue(self):
        now = s.time.time()
        for i in range(20): s.live_subscribe(f'SZ{i:06}')
        _, jobs = s._live_schedule(now + 1)
        self.assertEqual(len(jobs), 2)
        for c in s.LIVE_WANT: s.LIVE_WANT[c] = now + 1000
        _, jobs = s._live_schedule(now + 100)
        self.assertEqual(jobs, [])

    def test_network_gap_triggers_repair(self):
        e = self.entry(); e.update(day_valid=True, next_seq=100, last_live_seq=105, check_at=99999999999)
        with patch.object(s, '_live_fetch', return_value={'rows':['13:58:39,10,1,0,2'], 'seqs':[198]}):
            s._poll_one(self.code, e)
        self.assertLess(e['check_at'], s.time.time()+1)
        self.assertEqual(e['next_seq'], 100)  # latest tail is NOT a contiguous checkpoint

    def test_lunch_time_gap_with_contiguous_ids_is_not_loss(self):
        e=self.entry(); e.update(day_valid=True, next_seq=100, last_live_seq=99, check_at=99999999999)
        e['rows']['11:30:00']='11:30:00,10,1,0,2'
        with patch.object(s, '_live_fetch', return_value={'rows':['13:00:00,10,1,0,2'], 'seqs':[100]}):
            s._poll_one(self.code, e)
        self.assertEqual(e['check_at'], 99999999999)

    def test_periodic_reconciliation_even_without_detected_gap(self):
        e=self.entry(); e.update(next_seq=100, check_at=100)
        s.LIVE_WANT[self.code]=1000
        _, jobs=s._live_schedule(101)
        self.assertEqual(len(jobs), 1)

    def test_seed_exception_releases_claim(self):
        e=self.entry(); e['seed_inflight']=True; s._seed_active.add(self.code)
        with patch.object(s, '_live_validate_day', side_effect=RuntimeError('offline')):
            s._seed_code(self.code,e)
        self.assertFalse(e['seed_inflight']); self.assertNotIn(self.code,s._seed_active)

    def test_checkpoint_survives_restart_and_resumes_inside_page(self):
        e=self.entry(); e.update(next_seq=81,pre=10.03)
        e['rows']['10:04:00']='10:04:00,10,1,0,2'
        s._save_live(self.code,e); s.LIVE.clear()
        restored=self.entry()
        self.assertEqual(restored['next_seq'],81); self.assertEqual(restored['rows'],e['rows'])
        self.assertFalse(restored['day_valid']); self.assertEqual(restored['check_at'],0)

    def test_new_day_resets_cursor_and_rejects_old_worker(self):
        old=self.entry(); old.update(next_seq=81,day_valid=True)
        with patch.object(s,'_live_day',return_value='2026-10-09'):
            new=self.entry()
            with patch.object(s,'_live_fetch',return_value={'rows':['09:30:00,10,1,0,2']}):
                s._poll_one(self.code,old)
        self.assertEqual(new['next_seq'],0); self.assertEqual(new['rows'],{})

    def test_wrong_quote_day_cannot_seed(self):
        e=self.entry()
        with patch.object(s,'tencent_session',return_value={'day':'2026-10-07','pre':10}):
            self.assertFalse(s._live_validate_day(self.code,e))
        self.assertFalse(e['day_valid'])

    def test_missing_tail_never_marks_eastmoney_complete(self):
        e=self.entry()
        with patch.object(s,'_live_validate_day',return_value=True), \
             patch.object(s,'fetch_ticks_tencent_fullday',return_value={'Details':[], 'next_seq':0,'target':-1,'complete':False,'reason':'empty'}), \
             patch.object(s,'fetch_ticks',return_value={'Details':['10:00:00,10,1,0,2']}):
            s._seed_code(self.code,e)
        self.assertEqual(e['next_seq'],0); self.assertEqual(len(e['rows']),1)
        self.assertFalse(e['closed_checked'])

    def test_expired_subscription_gets_final_close_check(self):
        e=self.entry(); e['rows']['14:58:00']='14:58:00,10,1,0,2'
        with patch.object(s,'_live_after_close',return_value=True):
            _,jobs=s._live_schedule(s.time.time())
        self.assertEqual(len(jobs),1)

    def test_archive_failure_remains_retryable(self):
        e=self.entry();e['rows']['15:00:00']='15:00:00,10,1,0,2'
        result={'Details':[], 'next_seq':70,'target':69,'complete':True,'reason':'caught-up'}
        with patch.object(s,'_live_after_close',return_value=True), \
             patch.object(s,'_live_validate_day',return_value=True), \
             patch.object(s,'fetch_ticks_tencent_fullday',return_value=result), \
             patch.object(s,'write_ticks',side_effect=OSError('disk unavailable')):
            s._seed_code(self.code,e)
        self.assertFalse(e['closed_checked'])

    def test_preclose_job_cannot_certify_final_archive(self):
        e=self.entry()
        result={'Details':['14:59:00,10,1,0,2'], 'next_seq':1,'target':0,'complete':True,'reason':'caught-up'}
        with patch.object(s,'_live_validate_day',return_value=True), \
             patch.object(s,'fetch_ticks_tencent_fullday',return_value=result), \
             patch.object(s,'write_ticks') as archive:
            s._seed_code(self.code,e)
        archive.assert_not_called();self.assertFalse(e['closed_checked'])

    def test_same_day_seed_fills_middle_without_removing_tail(self):
        e=self.entry(); e['rows']['13:58:39']='13:58:39,10,1,0,2'
        with patch.object(s,'_live_validate_day',return_value=True), \
             patch.object(s,'fetch_ticks_tencent_fullday',return_value={'Details':['13:54:00,10,1,0,2'], 'next_seq':70,'target':69,'complete':True,'reason':'caught-up'}):
            s._seed_code(self.code,e)
        self.assertEqual(sorted(e['rows']),['13:54:00','13:58:39'])
        self.assertEqual(e['next_seq'],70)


if __name__=='__main__': unittest.main()
