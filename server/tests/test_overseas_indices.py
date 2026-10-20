"""Delayed benchmark snapshot: fixed symbols, bounded refresh, last-good values."""
import importlib.util
import json
from pathlib import Path
import threading
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('index_server', Path(__file__).parents[1] / 'scripts/server.py')
s = importlib.util.module_from_spec(spec)
spec.loader.exec_module(s)


def row(symbol='SP:S5INFT', price=6992.61):
    return {'s': symbol, 'd': [price, 1.18, 82.03, 7001.3, 7062.02, 6982.89, 'delayed_streaming_600']}


class Response:
    def __init__(self, rows): self.rows = rows
    def __enter__(self): return self
    def __exit__(self, *args): pass
    def read(self): return json.dumps({'data': self.rows}).encode()


class IndexTests(unittest.TestCase):
    def setUp(self):
        s._index_rows = {}; s._index_next = 0
        self.opener = patch.object(s.urllib.request, 'build_opener').start().return_value
        self.clock = patch.object(s.time, 'monotonic', return_value=100).start()
        patch.object(s, 'log').start()
        self.addCleanup(patch.stopall)

    def test_one_request_per_minute_and_fixed_payload(self):
        self.opener.open.return_value = Response([row()])
        self.assertEqual(s.overseas_indices()['data'][0]['d'][0], 6992.61)
        s.overseas_indices()
        self.assertEqual(self.opener.open.call_count, 1)
        request = self.opener.open.call_args.args[0]
        self.assertEqual(json.loads(request.data)['symbols']['tickers'], list(s.INDEX_SYMBOLS))
        self.clock.return_value = 161
        s.overseas_indices()
        self.assertEqual(self.opener.open.call_count, 2)

    def test_failure_retains_marked_values_and_backs_off(self):
        self.opener.open.return_value = Response([row()]); s.overseas_indices()
        self.clock.return_value = 161
        self.opener.open.side_effect = TimeoutError()
        result = s.overseas_indices()['data'][0]
        self.assertTrue(result['stale']); self.assertEqual(result['d'][0], 6992.61)
        s.overseas_indices(); self.assertEqual(self.opener.open.call_count, 2)

    def test_bad_rows_cannot_replace_last_good_or_add_unknown_symbols(self):
        self.opener.open.return_value = Response([row()]); s.overseas_indices()
        self.clock.return_value = 161
        self.opener.open.return_value = Response([row(price=0), row('NYSE:FAKE'), row(price=float('nan'))])
        result = s.overseas_indices()['data']
        self.assertEqual(len(result), 1); self.assertTrue(result[0]['stale'])

    def test_first_failure_and_inflight_request_do_not_fan_out(self):
        self.opener.open.side_effect = TimeoutError()
        self.assertIsNone(s.overseas_indices()); self.assertIsNone(s.overseas_indices())
        self.assertEqual(self.opener.open.call_count, 1)
        with s._index_lock:
            self.assertIsNone(s.overseas_indices())
        s._index_rows = {'SP:S5INFT': row()}
        with s._index_lock:
            self.assertEqual(len(s.overseas_indices()['data']), 1)

    def test_partial_snapshot_keeps_only_missing_row_stale(self):
        self.opener.open.return_value = Response([row(), row('ICEUS:NYFANG')]); s.overseas_indices()
        self.clock.return_value = 161
        self.opener.open.return_value = Response([row(price=7100)])
        result = {r['s']: r for r in s.overseas_indices()['data']}
        self.assertFalse(result['SP:S5INFT']['stale']); self.assertTrue(result['ICEUS:NYFANG']['stale'])
