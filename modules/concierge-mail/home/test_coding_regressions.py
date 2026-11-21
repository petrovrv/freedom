"""Regression tests for concurrent refresh, stale authorization and reply chronology."""
import concurrent.futures
import importlib
import threading
import tempfile
import time
import unittest


class CodingRegressionTests(unittest.TestCase):
    def test_task_3(self):
        g=importlib.import_module('gmailsync')
        calls=[]
        lock=threading.Lock()
        def request(*args):
            with lock:
                calls.append(args)
                n=len(calls)
            time.sleep(.08)
            return 200,{'access_token':'synthetic-'+str(n),'expires_in':3600}
        token=g.TokenSource(cred={'client_id':'fixture','client_secret':'fixture','refresh_token':'fixture'},request=request)
        def wave():
            barrier=threading.Barrier(8)
            def call(_):
                barrier.wait(timeout=5)
                return token()
            with concurrent.futures.ThreadPoolExecutor(8) as pool:
                return list(pool.map(call,range(8)))
        results=wave()
        self.assertEqual(len(calls),1,'eight simultaneous calls should cause one refresh')
        self.assertEqual(len(set(results)),1)
        token(); self.assertEqual(len(calls),1)
        token._until=0
        wave(); self.assertEqual(len(calls),2)
        failures=[True]
        def flaky(*args):
            if failures.pop() if failures else False:
                return 400,{'error':'invalid_grant'}
            return 200,{'access_token':'recovered','expires_in':3600}
        token=g.TokenSource(cred={'client_id':'fixture','client_secret':'fixture','refresh_token':'fixture'},request=flaky)
        with self.assertRaises(PermissionError):token()
        self.assertEqual(token(),'recovered')

    def test_task_4(self):
        g=importlib.import_module('gmailsync')
        refreshes=[]
        def refresh(*args):
            refreshes.append(1)
            return 200,{'access_token':'synthetic-'+str(len(refreshes)),'expires_in':3600}
        token=g.TokenSource(cred={'client_id':'fixture','client_secret':'fixture','refresh_token':'fixture'},request=refresh)
        self.assertEqual(token(),'synthetic-1')
        seen=[]
        def endpoint(method,url,headers,*args):
            seen.append(headers['Authorization'])
            return (401,{}) if headers['Authorization']=='Bearer synthetic-1' else (200,{'ok':True})
        client=g.Gmail(token,request=endpoint,sleep=lambda _:None)
        self.assertEqual(client.get('profile'),{'ok':True})
        self.assertEqual((len(refreshes),len(seen)),(2,2))
        calls=[]
        def always401(*args):calls.append(1);return 401,{}
        with self.assertRaises(Exception):g.Gmail(token,request=always401,sleep=lambda _:None).get('profile')
        self.assertLessEqual(len(calls),2,'401 recovery must be bounded to one retry')
        count=len(refreshes)
        with self.assertRaises(Exception):g.Gmail(token,request=lambda *a:(403,{}),sleep=lambda _:None).get('profile')
        self.assertEqual(len(refreshes),count,'ordinary forbidden response must not refresh')
        self.assertEqual(g.Gmail(lambda:'fixture',request=lambda *a:(200,{'ok':1})).get('profile'),{'ok':1})
        # All eight requests used token 1; seven old 401 responses arrive after
        # token 2 is already installed. They must not evict the newer token.
        refreshed=threading.Event();stale_wave=threading.Barrier(8)
        refresh_count=[];request_lock=threading.Lock();old_requests=[]
        def delayed_refresh(*args):
            with request_lock:
                refresh_count.append(1);index=len(refresh_count)
            if index>1:refreshed.set()
            return 200,{'access_token':'wave-'+str(index),'expires_in':3600}
        token=g.TokenSource(cred={'client_id':'fixture','client_secret':'fixture','refresh_token':'fixture'},request=delayed_refresh)
        self.assertEqual(token(),'wave-1')
        def concurrent_endpoint(method,url,headers,*args):
            if headers['Authorization']=='Bearer wave-1':
                with request_lock:old_requests.append(1);index=len(old_requests)
                stale_wave.wait(timeout=5)
                if index>1:
                    if not refreshed.wait(timeout=5):raise AssertionError('no first refresh')
                    time.sleep(.02)
                return 401,{}
            return 200,{'ok':True}
        client=g.Gmail(token,request=concurrent_endpoint,sleep=lambda _:None)
        with concurrent.futures.ThreadPoolExecutor(8) as pool:
            results=list(pool.map(lambda _:client.get('profile'),range(8)))
        self.assertEqual(results,[{'ok':True}]*8)
        self.assertEqual(len(refresh_count),2,'late stale responses must reuse the new token')

    def test_task_5(self):
        from email.message import EmailMessage
        from mailstore import Store
        from workflow import Workflow
        from gmailsync import _schema
        def raw(mid,parent,day):
            m=EmailMessage();m['From']='a@example.org';m['To']='b@example.org'
            m['Subject']='Synthetic chronology';m['Message-ID']='<'+mid+'@example.org>'
            m['Date']=f'{day:02d} Nov 2026 12:00:00 +0000'
            if parent:m['In-Reply-To']='<'+parent+'@example.org>'
            m.set_content('Fixture for chronological ordering.');return m.as_bytes()
        fixtures={'a':(None,1,False),'b':('a',2,True),'c':('b',3,False),'d':('c',4,True)}
        for answered in [False,True]:
            mids=['a','b','c']+(['d'] if answered else [])
            for order,gmail in [(mids,True),(list(reversed(mids)),True),(list(reversed(mids)),False)]:
                with self.subTest(answered=answered,order=order,gmail=gmail),tempfile.TemporaryDirectory() as temp:
                    store=Store(temp)
                    try:
                        if gmail:_schema(store)
                        flow=Workflow(store)
                        for mid in order:
                            parent,day,sent=fixtures[mid]
                            row=store.ingest('fixture@example.org',raw(mid,parent,day),sent=sent)
                            if gmail:
                                with store.db:
                                    store.db.execute('INSERT INTO gmail_messages(account,gmail_id,thread_id,message_id,internal_date) VALUES (?,?,?,?,?)',('fixture@example.org',mid,'t',row['id'],1793534400000+day*86400000))
                        thread=store.db.execute("SELECT thread_id FROM messages WHERE message_id='<c@example.org>'").fetchone()[0]
                        self.assertEqual(flow.already_answered(thread),answered)
                    finally:store.close()
        # Unknown chronology cannot safely prove all pending incoming mail answered.
        for answered_mid,invalid_mid in [('a','c'),('c','c')]:
            with self.subTest(unknown_dates=True,answered_mid=answered_mid),tempfile.TemporaryDirectory() as temp:
                store=Store(temp)
                try:
                    flow=Workflow(store)
                    first=store.ingest('fixture@example.org',raw('a',None,1))
                    second=store.ingest('fixture@example.org',raw('c','a',3))
                    with store.db:
                        store.db.execute('UPDATE messages SET date=? WHERE message_id=?',('invalid-date','<'+invalid_mid+'@example.org>'))
                        store.db.execute('INSERT INTO sent_replies(account,parent_mid) VALUES (?,?)',('fixture@example.org','<'+answered_mid+'@example.org>'))
                    self.assertFalse(flow.already_answered(second['thread_id']))
                finally:store.close()
        with self.subTest(account_isolation=True),tempfile.TemporaryDirectory() as temp:
            store=Store(temp)
            try:
                flow=Workflow(store)
                a=store.ingest('one@example.org',raw('a',None,1))
                b=store.ingest('two@example.org',raw('a',None,1))
                with store.db:store.db.execute('INSERT INTO sent_replies(account,parent_mid) VALUES (?,?)',('two@example.org','<a@example.org>'))
                self.assertFalse(flow.already_answered(a['thread_id']))
                self.assertTrue(flow.already_answered(b['thread_id']))
            finally:store.close()

if __name__ == '__main__':
    unittest.main()
