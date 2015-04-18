﻿//
//  CouchbaseLiteServiceListener.cs
//
//  Author:
//  	Jim Borden  <jim.borden@couchbase.com>
//
//  Copyright (c) 2015 Couchbase, Inc All rights reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//  http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//
using System;
using System.Net;

using Couchbase.Lite.Util;
using System.Net.Http;

namespace Couchbase.Lite.PeerToPeer
{
    public sealed class CouchbaseLiteServiceListener : IDisposable
    {
        private readonly HttpListener _listener;
        internal readonly CouchbaseLiteRouter _router;
        private bool _disposed;

        public bool ReadOnly {
            get {
                return _readOnly;
            }
            set {
                if (value) {
                    _router.OnAccessCheck = (method, endpoint) =>
                    {
                        if(method.Equals(HttpMethod.Head) || method.Equals(HttpMethod.Get)) {
                            return new Status(StatusCode.Ok);
                        } 
                        if(method.Equals(HttpMethod.Post) && (endpoint.EndsWith("_all_docs") || endpoint.EndsWith("_revs_diff"))) {
                            return new Status(StatusCode.Ok);
                        }

                        return new Status(StatusCode.Forbidden);
                    };
                } else {
                    _router.OnAccessCheck = null;
                }
                _readOnly = value;
            }
        }
        private bool _readOnly;

        public CouchbaseLiteServiceListener(Manager manager, int port)
        {
            _listener = new HttpListener();
            _router = new CouchbaseLiteRouter(manager);
            string prefix = String.Format("http://*:{0}/", port);
            _listener.Prefixes.Add(prefix);
        }

        public void Start()
        {
            if (_listener.IsListening) {
                return;
            }
                
            _listener.Start();
            _listener.GetContextAsync().ContinueWith((t) => ProcessContext(t.Result));
        }

        public void Stop()
        {
            if (!_listener.IsListening) {
                return;
            }
                
            _listener.Stop();
        }

        public void Abort()
        {
            if (!_listener.IsListening) {
                return;
            }
                
            _listener.Abort();
        }

        private void ProcessContext(HttpListenerContext context)
        {
            _listener.GetContextAsync().ContinueWith((t) => ProcessContext(t.Result));
            _router.HandleContext(context);
        }

        public void Dispose()
        {
            if (_disposed) {
                return;
            }

            _disposed = true;
            ((IDisposable)_listener).Dispose();
        }
    }
}

