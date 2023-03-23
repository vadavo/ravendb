﻿using System;
using System.Collections.Generic;
using Raven.Server.ServerWide.Context;

namespace Raven.Server.Documents.Indexes.Workers
{
    public abstract partial class HandleReferencesBase
    {
        private class ReferencesState
        {
            private readonly Dictionary<string, ReferenceState> _lastIdPerCollectionForDocuments = new Dictionary<string, ReferenceState>();

            private readonly Dictionary<string, ReferenceState> _lastIdPerCollectionForTombstones = new Dictionary<string, ReferenceState>();

#if DEBUG
            private readonly HashSet<(ActionType, string)> _setCollections = new HashSet<(ActionType, string)>();
#endif

            public ReferenceState For(ActionType actionType, string collection)
            {
                var dictionary = GetDictionary(actionType);
                return dictionary.TryGetValue(collection, out var referenceState) ? referenceState : null;
            }

            public InMemoryReferencesInfo GetReferencesInfo(string collection)
            {
                return new InMemoryReferencesInfo
                {
                    ParentItemEtag = For(ActionType.Document, collection)?.GetLastIndexedParentEtag() ?? 0,
                    ParentTombstoneEtag = For(ActionType.Tombstone, collection)?.GetLastIndexedParentEtag() ?? 0,
                };
            }

            public void Clear(ActionType actionType)
            {
                var dictionary = GetDictionary(actionType);
                dictionary.Clear();
            }

            public void Set(ActionType actionType, string collection, ReferenceState referenceState, TransactionOperationContext indexContext)
            {
                var dictionary = GetDictionary(actionType);

                indexContext.Transaction.InnerTransaction.LowLevelTransaction.AfterCommitWhenNewTransactionsPrevented += _ =>
                {
                    // we update this only after the transaction was committed
                    dictionary[collection] = referenceState;

#if DEBUG
                    if (_setCollections.Add((actionType, collection)) == false)
                        throw new InvalidOperationException($"Double set of collection {collection} of action type {actionType}");
#endif
                };

#if DEBUG
                indexContext.Transaction.InnerTransaction.LowLevelTransaction.OnDispose += _ =>
                {
                    _setCollections.Remove((actionType, collection));
                };
#endif
            }

            public void Clear(bool earlyExit, ActionType actionType, string collection, TransactionOperationContext indexContext)
            {
                if (earlyExit)
                    return;

                var dictionary = GetDictionary(actionType);
                if (dictionary.Count == 0)
                    return;

                indexContext.Transaction.InnerTransaction.LowLevelTransaction.AfterCommitWhenNewTransactionsPrevented += _ =>
                {
                    // we update this only after the transaction was committed
                    dictionary.Remove(collection);
                };
            }

            private Dictionary<string, ReferenceState> GetDictionary(ActionType actionType)
            {
                return actionType == ActionType.Document
                    ? _lastIdPerCollectionForDocuments
                    : _lastIdPerCollectionForTombstones;
            }

            public class ReferenceState
            {
                private readonly string _referencedItemId;
                private readonly string _nextItemId;
                private readonly long _referencedItemEtag;
                private readonly long _lastIndexedParentEtag;

                public ReferenceState(string referencedItemId, long referenceEtag, string itemId, long lastIndexedParentEtag)
                {
                    _referencedItemId = referencedItemId;
                    _referencedItemEtag = referenceEtag;
                    _nextItemId = itemId;
                    _lastIndexedParentEtag = lastIndexedParentEtag;
                }

                public string GetLastProcessedItemId(Reference referencedDocument)
                {
                    if (referencedDocument.Key == _referencedItemId && referencedDocument.Etag == _referencedItemEtag)
                        return _nextItemId;

                    return null;
                }

                public long GetLastIndexedParentEtag()
                {
                    return _lastIndexedParentEtag;
                }
            }
        }
    }
}
