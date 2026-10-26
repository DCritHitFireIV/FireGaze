"""Offline CLI regression coverage for full description retranslation."""
import contextlib
import copy
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
import warnings
from unittest.mock import patch

import ffxiv_glossary
import update_translations as updater


class DescriptionRecheckTests(unittest.TestCase):
    def run_update(self, *, recheck=False, user=False, response='显示完整的插件说明。'):
        # The regression is the write-back branch discarding a successful recheck
        # for unchanged originals. API/network boundaries alone are substituted;
        # queueing, merge policy and JSON persistence are the real CLI code.
        pair = {'Original': 'Shows the complete plugin description.',
                'Translated': '旧的简略译文', 'Source': 'user' if user else 'ai'}
        table = {'Example': {
            'Name': {'Original': 'Example', 'Translated': 'Example'},
            'Punchline': {'Original': 'A plugin description', 'Translated': '旧简介', 'Source': 'ai'},
            'Description': pair,
        }}
        corpus = {'Example': {'name': 'Example', 'punchline': 'A plugin description',
                              'description': pair['Original'], 'repo_url': 'https://example.test/repo.json'}}
        with tempfile.TemporaryDirectory() as temp:
            target = Path(temp) / 'translations.json'
            (Path(temp) / 'docs').mkdir()
            target.write_text(json.dumps(table), encoding='utf-8')
            def fake_api(key, system, payload, max_tokens):
                return json.dumps({'items': [{'id': 0, 'p': '插件简介', 'd': response}]})
            with patch.dict(os.environ, {'DEEPSEEK_API_KEY': 'offline-test'}), \
                 patch.object(updater, 'build_corpus', return_value=copy.deepcopy(corpus)), \
                 patch.object(updater, 'add_official_corpus'), \
                 patch.object(ffxiv_glossary, 'build_glossary', return_value={}), \
                 patch.object(updater, 'deepseek', side_effect=fake_api), \
                 contextlib.redirect_stdout(io.StringIO()):
                args = ['--table', str(target)] + (['--recheck'] if recheck else [])
                self.assertEqual(updater.main(args), 0)
            return json.loads(target.read_text(encoding='utf-8'))['Example']

    def test_recheck_persists_fresh_translation_for_unchanged_ai_original(self):
        result = self.run_update(recheck=True)
        self.assertEqual(result['Description']['Translated'], '显示完整的插件说明。')
        self.assertEqual(result['Punchline']['Translated'], '插件简介')

    def test_incremental_update_preserves_existing_translation(self):
        result = self.run_update()
        self.assertEqual(result['Description']['Translated'], '旧的简略译文')

    def test_recheck_never_overwrites_user_description(self):
        result = self.run_update(recheck=True, user=True)
        self.assertEqual(result['Description']['Translated'], '旧的简略译文')
        self.assertEqual(result['Description']['Source'], 'user')

    def test_empty_recheck_result_preserves_existing_translation(self):
        result = self.run_update(recheck=True, response='')
        self.assertEqual(result['Description']['Translated'], '旧的简略译文')

    def test_echoed_recheck_result_preserves_existing_translation(self):
        result = self.run_update(recheck=True, response='Shows the complete plugin description.')
        self.assertEqual(result['Description']['Translated'], '旧的简略译文')

    def test_table_reader_does_not_leak_an_open_file(self):
        with warnings.catch_warnings(record=True) as recorded:
            warnings.simplefilter('always', ResourceWarning)
            self.run_update(recheck=True)
        self.assertFalse([w for w in recorded if issubclass(w.category, ResourceWarning)])


if __name__ == '__main__':
    unittest.main()
