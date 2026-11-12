import sys
import unittest
from unittest.mock import patch
from subprocess import CompletedProcess
sys.path.insert(0, r'C:\AIWorkbench\tools\devtree')
import codex_models as m

class Models(unittest.TestCase):
    def setUp(self):
        self.rows=[{'model':'renamed','isDefault':True,'supportedReasoningEfforts':[{'reasoningEffort':'high'}],'defaultReasoningEffort':'high'}, {'model':'alternate'}]
    def test_removed_preference(self):
        self.assertEqual(m.choose(self.rows,'old-name'),('renamed','high'))
    def test_explicit_available(self):
        self.assertEqual(m.choose(self.rows,'alternate'),('alternate',None))
    def test_hidden_and_empty(self):
        with self.assertRaises(RuntimeError): m.choose([{'model':'hidden','hidden':True}])
    def test_refresh_retry(self):
        fail=CompletedProcess([],1,'','model_not_found')
        ok=CompletedProcess([],0,'ok','')
        with patch.object(m,'catalog',side_effect=[self.rows,[{'model':'new-default','isDefault':True}]]), patch.object(m.subprocess,'run',side_effect=[fail,ok]) as run:
            self.assertIs(m.run_review('codex','.',None,lambda model,effort:[model],'prompt',10,lambda s:None),ok)
            self.assertEqual([c.args[0] for c in run.call_args_list],[['renamed'],['new-default']])
    def test_no_auth_retry(self):
        fail=CompletedProcess([],1,'','unauthorized')
        with patch.object(m,'catalog',return_value=self.rows), patch.object(m.subprocess,'run',return_value=fail) as run:
            self.assertIs(m.run_review('codex','.',None,lambda a,b:[a],'',10,lambda s:None),fail)
            self.assertEqual(run.call_count,1)
    def test_retry_excludes_failed_and_is_bounded(self):
        fail=CompletedProcess([],1,'','model_not_found')
        with patch.object(m,'catalog',return_value=self.rows), patch.object(m.subprocess,'run',return_value=fail) as run:
            self.assertIs(m.run_review('codex','.',None,lambda a,b:[a],'',10,lambda s:None),fail)
            self.assertEqual([c.args[0] for c in run.call_args_list],[['renamed'],['alternate']])

if __name__=='__main__': unittest.main()
