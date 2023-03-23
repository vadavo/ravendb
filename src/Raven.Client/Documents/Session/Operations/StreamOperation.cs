using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Raven.Client.Documents.Commands;
using Raven.Client.Documents.Queries;
using Raven.Client.Documents.Queries.TimeSeries;
using Raven.Client.Exceptions.Documents.Indexes;
using Raven.Client.Util;
using Sparrow;
using Sparrow.Json;
using Sparrow.Json.Parsing;
using static Sparrow.Extensions.RavenDateTimeExtensions;

namespace Raven.Client.Documents.Session.Operations
{
    internal class TimeSeriesStreamOperation : StreamOperation
    {
        private readonly string _docId;
        private readonly string _name;
        private readonly DateTime? _from;
        private readonly DateTime? _to;
        private readonly TimeSpan? _offset;

        public TimeSeriesStreamOperation(InMemoryDocumentSessionOperations session, string docId, string name, DateTime? from = null, DateTime? to = null, TimeSpan? offset = null) : base(session)
        {
            if (string.IsNullOrEmpty(docId))
                throw new ArgumentNullException(nameof(docId));
            if (string.IsNullOrEmpty(name))
                throw new ArgumentNullException(nameof(name));

            _docId = docId;
            _name = name;
            _from = from;
            _to = to;
            _offset = offset;
        }

        public TimeSeriesStreamOperation(InMemoryDocumentSessionOperations session, StreamQueryStatistics statistics, string docId, string name, DateTime? from = null, DateTime? to = null, TimeSpan? offset = null) : base(session, statistics)
        {
            if (string.IsNullOrEmpty(docId))
                throw new ArgumentNullException(nameof(docId));
            if (string.IsNullOrEmpty(name))
                throw new ArgumentNullException(nameof(name));

            _docId = docId;
            _name = name;
            _from = from;
            _to = to;
            _offset = offset;
        }

        public StreamCommand CreateRequest()
        {
            var sb = new StringBuilder("streams/timeseries?");

            sb.Append("docId=").Append(Uri.EscapeDataString(_docId)).Append('&');
            sb.Append("name=").Append(Uri.EscapeDataString(_name)).Append('&');

            if (_from.HasValue)
                sb.Append("from=").Append(_from.Value.EnsureUtc().GetDefaultRavenFormat()).Append('&');

            if (_to.HasValue)
                sb.Append("to=").Append(_to.Value.EnsureUtc().GetDefaultRavenFormat()).Append('&');

            if (_offset.HasValue)
                sb.Append("offset=").Append(_offset).Append('&');

            return new StreamCommand(sb.ToString());
        }
    }

    internal class StreamOperation
    {
        private readonly InMemoryDocumentSessionOperations _session;
        private readonly StreamQueryStatistics _statistics;
        private bool _isQueryStream;

        public StreamOperation(InMemoryDocumentSessionOperations session)
        {
            _session = session;
        }

        public StreamOperation(InMemoryDocumentSessionOperations session, StreamQueryStatistics statistics)
        {
            _session = session;
            _statistics = statistics;
        }

        public QueryStreamCommand CreateRequest(IndexQuery query)
        {
            _isQueryStream = true;

            if (query.WaitForNonStaleResults)
                throw new NotSupportedException(
                    "Since Stream() does not wait for indexing (by design), streaming query with WaitForNonStaleResults is not supported.");

            _session.IncrementRequestCount();

            return new QueryStreamCommand(_session.Conventions, query);
        }

        public void EnsureIsAcceptable(string indexName, StreamResult result)
        {
            if (result != null)
                return;

            if (indexName != null)
                IndexDoesNotExistException.ThrowFor(indexName);
        }

        public StreamCommand CreateRequest(string startsWith, string matches, int start, int pageSize, string exclude, string startAfter = null)
        {
            var sb = new StringBuilder("streams/docs?");

            if (startsWith != null)
            {
                sb.Append("startsWith=").Append(Uri.EscapeDataString(startsWith)).Append("&");
            }
            if (matches != null)
            {
                sb.Append("matches=").Append(Uri.EscapeDataString(matches)).Append("&");
            }
            if (exclude != null)
            {
                sb.Append("exclude=").Append(Uri.EscapeDataString(exclude)).Append("&");
            }
            if (startAfter != null)
            {
                sb.Append("startAfter=").Append(Uri.EscapeDataString(startAfter)).Append("&");
            }

            if (start != 0)
                sb.Append("start=").Append(start).Append("&");

            if (pageSize != int.MaxValue)
                sb.Append("pageSize=").Append(pageSize).Append("&");

            return new StreamCommand(sb.ToString());
        }

        internal YieldStreamResults SetResultForTimeSeries(StreamResult response)
        {
            var enumerator = new YieldStreamResults(_session, response, _isQueryStream, isTimeSeriesStream: true, isAsync: false, _statistics);
            enumerator.Initialize();

            return enumerator;
        }

        internal async Task<YieldStreamResults> SetResultForTimeSeriesAsync(StreamResult response)
        {
            var enumerator = new YieldStreamResults(_session, response, _isQueryStream, isTimeSeriesStream: true, isAsync: true, _statistics);
            await enumerator.InitializeAsync().ConfigureAwait(false);

            return enumerator;
        }

        public IEnumerator<BlittableJsonReaderObject> SetResult(StreamResult response)
        {
            var enumerator = new YieldStreamResults(_session, response, _isQueryStream, isTimeSeriesStream: false, isAsync: false, _statistics);
            enumerator.Initialize();

            return enumerator;
        }

        public async Task<YieldStreamResults> SetResultAsync(StreamResult response, CancellationToken token = default)
        {
            var enumerator = new YieldStreamResults(_session, response, _isQueryStream, isTimeSeriesStream: false, isAsync: true, _statistics, token);
            await enumerator.InitializeAsync().ConfigureAwait(false);

            return enumerator;
        }

        internal class TimeSeriesStreamEnumerator : IAsyncEnumerator<BlittableJsonReaderObject>, IEnumerator<BlittableJsonReaderObject>
        {
            private readonly JsonOperationContext _context;
            private readonly PeepingTomStream _peepingTomStream;
            private readonly UnmanagedJsonParser _parser;
            private readonly JsonParserState _state;
            private readonly JsonOperationContext.MemoryBuffer _buffer;
            private readonly CancellationToken _token;
            private BlittableJsonReaderObject _current;

            public TimeSeriesStreamEnumerator(JsonOperationContext context, PeepingTomStream peepingTomStream, UnmanagedJsonParser parser, JsonParserState state, JsonOperationContext.MemoryBuffer buffer, CancellationToken token = default)
            {
                _context = context;
                _peepingTomStream = peepingTomStream;
                _parser = parser;
                _state = state;
                _buffer = buffer;
                _token = token;
            }

            public void Initialize()
            {
                AsyncHelpers.RunSync(InitializeAsync);
            }

            public async Task InitializeAsync()
            {
                var property = UnmanagedJsonParserHelper.ReadString(_context, _peepingTomStream, _parser, _state, _buffer);

                if (property.StartsWith(Constants.TimeSeries.QueryFunction) == false)
                    UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);

                if (await UnmanagedJsonParserHelper.ReadAsync(_peepingTomStream, _parser, _state, _buffer, _token).ConfigureAwait(false) == false)
                    UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);

                if (_state.CurrentTokenType != JsonParserToken.StartArray)
                    UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);
            }

            private bool _done;
            private bool _disposed;

            public async ValueTask DisposeAsync()
            {
                if (_disposed)
                    return;

                if (_done == false)
                {
                    while (await MoveNextAsync().ConfigureAwait(false))
                    {
                        // we need to consume the rest of the stream, before we can move next the outer enumerator
                    }
                }

                if (await UnmanagedJsonParserHelper.ReadAsync(_peepingTomStream, _parser, _state, _buffer, _token).ConfigureAwait(false) == false)
                    UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);

                if (_state.CurrentTokenType != JsonParserToken.EndObject)
                    UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);

                _disposed = true;
            }

            public async ValueTask<bool> MoveNextAsync()
            {
                if (await UnmanagedJsonParserHelper.ReadAsync(_peepingTomStream, _parser, _state, _buffer, _token).ConfigureAwait(false) == false)
                    UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);

                if (_state.CurrentTokenType == JsonParserToken.EndArray)
                {
                    _done = true;
                    _current = null;
                    return false;
                }

                using (var builder = new BlittableJsonDocumentBuilder(_context, BlittableJsonDocumentBuilder.UsageMode.None, "readArray/singleResult", _parser, _state))
                {
                    await UnmanagedJsonParserHelper.ReadObjectAsync(builder, _peepingTomStream, _parser, _buffer, _token).ConfigureAwait(false);

                    _current = builder.CreateReader();
                    return true;
                }
            }

            public bool MoveNext()
            {
                return AsyncHelpers.RunSync(MoveNextAsync().AsTask);
            }

            public void Reset()
            {
                throw new NotSupportedException("Enumerator does not support resetting");
            }

            BlittableJsonReaderObject IEnumerator<BlittableJsonReaderObject>.Current => _current;

            BlittableJsonReaderObject IAsyncEnumerator<BlittableJsonReaderObject>.Current => _current;

            object IEnumerator.Current => _current;

            public void Dispose()
            {
                AsyncHelpers.RunSync(DisposeAsync().AsTask);
            }
        }

        internal class YieldStreamResults : IAsyncEnumerator<BlittableJsonReaderObject>, IEnumerator<BlittableJsonReaderObject>
        {
            public YieldStreamResults(InMemoryDocumentSessionOperations session, StreamResult response, bool isQueryStream, bool isTimeSeriesStream, bool isAsync, StreamQueryStatistics streamQueryStatistics, CancellationToken token = default)
            {
                _response = response ?? throw new InvalidOperationException("The index does not exists, failed to stream results");
                _returnContext = session.RequestExecutor.ContextPool.AllocateOperationContext(out _builderContext);
                _peepingTomStream = new PeepingTomStream(_response.Stream, session.Context);
                _session = session;
                _isQueryStream = isQueryStream;
                _isAsync = isAsync;
                _streamQueryStatistics = streamQueryStatistics;
                _maxDocsCountOnCachedRenewSession = session._maxDocsCountOnCachedRenewSession;
                _token = token;
                _isTimeSeriesStream = isTimeSeriesStream;
            }

            private readonly StreamResult _response;
            private readonly InMemoryDocumentSessionOperations _session;
            private readonly int _maxDocsCountOnCachedRenewSession;
            private JsonParserState _state;
            private UnmanagedJsonParser _parser;
            private JsonOperationContext.MemoryBuffer _buffer;
            private bool _initialized;
            private JsonOperationContext.MemoryBuffer.ReturnBuffer _returnBuffer;
            private readonly bool _isQueryStream;
            private readonly bool _isAsync;
            private readonly bool _isTimeSeriesStream;
            private readonly StreamQueryStatistics _streamQueryStatistics;
            private readonly CancellationToken _token;
            private readonly PeepingTomStream _peepingTomStream;
            private int _docsCountOnCachedRenewSession;
            private readonly JsonOperationContext _builderContext;
            private readonly IDisposable _returnContext;
            private BlittableJsonDocumentBuilder _builder;

#if NETSTANDARD2_0 || NETCOREAPP2_1
            public ValueTask DisposeAsync()
#else

            public async ValueTask DisposeAsync()
#endif
            {
#if NETSTANDARD2_0 || NETCOREAPP2_1
                Dispose();
                return default;
#else
                await _response.Stream.DisposeAsync().ConfigureAwait(false);

                DisposeInternal();
#endif
            }

            public void Dispose()
            {
                _response.Stream.Dispose();

                DisposeInternal();
            }

            private void DisposeInternal()
            {
                _response.Response.Dispose();
                _parser.Dispose();
                _returnBuffer.Dispose();
                _peepingTomStream.Dispose();
                _builder.Dispose();
                _returnContext.Dispose();
            }

            public bool MoveNext()
            {
                AssertInitialized();

                CheckIfContextOrCacheNeedToBeRenewed();

                _timeSeriesIt?.Dispose();

                if (UnmanagedJsonParserHelper.Read(_peepingTomStream, _parser, _state, _buffer) == false)
                    UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);

                if (_state.CurrentTokenType == JsonParserToken.EndArray)
                {
                    if (UnmanagedJsonParserHelper.Read(_peepingTomStream, _parser, _state, _buffer) == false)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);

                    if (_state.CurrentTokenType != JsonParserToken.EndObject)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);

                    return false;
                }

                _builder.Renew("readArray/singleResult", BlittableJsonDocumentBuilder.UsageMode.ToDisk);

                if (_isTimeSeriesStream)
                    UnmanagedJsonParserHelper.ReadProperty(_builder, _peepingTomStream, _parser, _buffer);
                else
                    UnmanagedJsonParserHelper.ReadObject(_builder, _peepingTomStream, _parser, _buffer);

                Current = _builder.CreateReader();

                _builder.Reset();

                if (_isTimeSeriesStream)
                {
                    _timeSeriesIt = new TimeSeriesStreamEnumerator(_builderContext, _peepingTomStream, _parser, _state, _buffer);
                    _timeSeriesIt.Initialize();
                }

                return true;
            }

            public async ValueTask<bool> MoveNextAsync()
            {
                AssertInitialized();

                CheckIfContextOrCacheNeedToBeRenewed();

                if (_timeSeriesIt != null)
                    await _timeSeriesIt.DisposeAsync().ConfigureAwait(false);

                if (await UnmanagedJsonParserHelper.ReadAsync(_peepingTomStream, _parser, _state, _buffer, _token).ConfigureAwait(false) == false)
                    UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);

                if (_state.CurrentTokenType == JsonParserToken.EndArray)
                {
                    if (await UnmanagedJsonParserHelper.ReadAsync(_peepingTomStream, _parser, _state, _buffer, _token).ConfigureAwait(false) == false)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);

                    if (_state.CurrentTokenType != JsonParserToken.EndObject)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);

                    return false;
                }

                _builder.Renew("readArray/singleResult", BlittableJsonDocumentBuilder.UsageMode.ToDisk);

                if (_isTimeSeriesStream)
                    await UnmanagedJsonParserHelper.ReadPropertyAsync(_builder, _peepingTomStream, _parser, _buffer, _token).ConfigureAwait(false);
                else
                    await UnmanagedJsonParserHelper.ReadObjectAsync(_builder, _peepingTomStream, _parser, _buffer, _token).ConfigureAwait(false);

                Current = _builder.CreateReader();

                _builder.Reset();

                if (_isTimeSeriesStream)
                {
                    _timeSeriesIt = new TimeSeriesStreamEnumerator(_builderContext, _peepingTomStream, _parser, _state, _buffer);
                    await _timeSeriesIt.InitializeAsync().ConfigureAwait(false);
                }
                return true;
            }

            private TimeSeriesStreamEnumerator _timeSeriesIt;

            public void ExposeTimeSeriesStream(ITimeSeriesQueryStreamResult result)
            {
                result.SetStream(_timeSeriesIt);
            }

            public void Initialize()
            {
                AssertNotAsync();

                try
                {
                    _initialized = true;

                    _state = new JsonParserState();
                    _parser = new UnmanagedJsonParser(_session.Context, _state, "stream contents");
                    _builder = new BlittableJsonDocumentBuilder(_builderContext, BlittableJsonDocumentBuilder.UsageMode.ToDisk, "readArray/singleResult", _parser, _state);
                    _returnBuffer = _session.Context.GetMemoryBuffer(out _buffer);

                    if (UnmanagedJsonParserHelper.Read(_peepingTomStream, _parser, _state, _buffer) == false)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);

                    if (_state.CurrentTokenType != JsonParserToken.StartObject)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);

                    if (_isQueryStream)
                        HandleStreamQueryStats(_session.Context, _response, _parser, _state, _buffer, _streamQueryStatistics);

                    var property = UnmanagedJsonParserHelper.ReadString(_session.Context, _peepingTomStream, _parser, _state, _buffer);

                    if (string.Equals(property, "Results") == false)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);

                    if (UnmanagedJsonParserHelper.Read(_peepingTomStream, _parser, _state, _buffer) == false)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);

                    if (_state.CurrentTokenType != JsonParserToken.StartArray)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);
                }
                catch
                {
                    Dispose();

                    throw;
                }
            }

            public async Task InitializeAsync()
            {
                AssertNotSync();

                try
                {
                    _initialized = true;

                    _state = new JsonParserState();
                    _parser = new UnmanagedJsonParser(_session.Context, _state, "stream contents");
                    _builder = new BlittableJsonDocumentBuilder(_builderContext, BlittableJsonDocumentBuilder.UsageMode.ToDisk, "readArray/singleResult", _parser, _state);
                    _returnBuffer = _session.Context.GetMemoryBuffer(out _buffer);

                    if (await UnmanagedJsonParserHelper.ReadAsync(_peepingTomStream, _parser, _state, _buffer, _token).ConfigureAwait(false) == false)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);

                    if (_state.CurrentTokenType != JsonParserToken.StartObject)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);

                    if (_isQueryStream)
                        HandleStreamQueryStats(_builderContext, _response, _parser, _state, _buffer, _streamQueryStatistics);

                    var property = UnmanagedJsonParserHelper.ReadString(_builderContext, _peepingTomStream, _parser, _state, _buffer);

                    if (string.Equals(property, "Results") == false)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);

                    if (await UnmanagedJsonParserHelper.ReadAsync(_peepingTomStream, _parser, _state, _buffer, _token).ConfigureAwait(false) == false)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);

                    if (_state.CurrentTokenType != JsonParserToken.StartArray)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(_peepingTomStream);
                }
                catch
                {
                    await DisposeAsync().ConfigureAwait(false);

                    throw;
                }
            }

            public void Reset()
            {
                throw new NotSupportedException("Enumerator does not support resetting");
            }

            public BlittableJsonReaderObject Current { get; private set; }

            object IEnumerator.Current => Current;

            private static void HandleStreamQueryStats(JsonOperationContext context, StreamResult response, UnmanagedJsonParser parser, JsonParserState state, JsonOperationContext.MemoryBuffer buffer, StreamQueryStatistics streamQueryStatistics = null)
            {
                using (var peepingTomStream = new PeepingTomStream(response.Stream, context))
                {
                    var property = UnmanagedJsonParserHelper.ReadString(context, peepingTomStream, parser, state, buffer);
                    if (string.Equals(property, nameof(StreamQueryStatistics.ResultEtag)) == false)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(peepingTomStream);
                    var resultEtag = UnmanagedJsonParserHelper.ReadLong(context, peepingTomStream, parser, state, buffer);

                    property = UnmanagedJsonParserHelper.ReadString(context, peepingTomStream, parser, state, buffer);
                    if (string.Equals(property, nameof(StreamQueryStatistics.IsStale)) == false)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(peepingTomStream);

                    if (UnmanagedJsonParserHelper.Read(peepingTomStream, parser, state, buffer) == false)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(peepingTomStream);

                    if (state.CurrentTokenType != JsonParserToken.False && state.CurrentTokenType != JsonParserToken.True)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(peepingTomStream);
                    var isStale = state.CurrentTokenType != JsonParserToken.False;

                    property = UnmanagedJsonParserHelper.ReadString(context, peepingTomStream, parser, state, buffer);
                    if (string.Equals(property, nameof(StreamQueryStatistics.IndexName)) == false)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(peepingTomStream);
                    var indexName = UnmanagedJsonParserHelper.ReadString(context, peepingTomStream, parser, state, buffer);

                    property = UnmanagedJsonParserHelper.ReadString(context, peepingTomStream, parser, state, buffer);
                    if (string.Equals(property, nameof(StreamQueryStatistics.TotalResults)) == false)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(peepingTomStream);
                    var totalResults = (int)UnmanagedJsonParserHelper.ReadLong(context, peepingTomStream, parser, state, buffer);

                    property = UnmanagedJsonParserHelper.ReadString(context, peepingTomStream, parser, state, buffer);
                    if (string.Equals(property, nameof(StreamQueryStatistics.IndexTimestamp)) == false)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(peepingTomStream);
                    var indexTimestamp = UnmanagedJsonParserHelper.ReadString(context, peepingTomStream, parser, state, buffer);

                    if (streamQueryStatistics == null)
                        return;

                    streamQueryStatistics.IndexName = indexName;
                    streamQueryStatistics.IsStale = isStale;
                    streamQueryStatistics.TotalResults = totalResults;
                    streamQueryStatistics.ResultEtag = resultEtag;

                    if (DateTime.TryParseExact(indexTimestamp, "o", CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out DateTime timeStamp) == false)
                        UnmanagedJsonParserHelper.ThrowInvalidJson(peepingTomStream);
                    streamQueryStatistics.IndexTimestamp = timeStamp;
                }
            }

            private void CheckIfContextOrCacheNeedToBeRenewed()
            {
                if (_builderContext.AllocatedMemory > 4 * 1024 * 1024 ||
                    _docsCountOnCachedRenewSession > _maxDocsCountOnCachedRenewSession)
                {
                    _builderContext.Reset();
                    _builderContext.Renew();
                    _docsCountOnCachedRenewSession = 0;
                    return;
                }

                if (_builder.NeedClearPropertiesCache())
                {
                    _builderContext.CachedProperties.ClearRenew();
                    return;
                }

                ++_docsCountOnCachedRenewSession;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void AssertInitialized()
            {
                if (_initialized == false)
                    throw new InvalidOperationException("Enumerator is not initialized. Please initialize it first.");
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void AssertNotSync()
            {
                if (_isAsync == false)
                    throw new InvalidOperationException("Cannot use asynchronous operations in synchronous enumerator");
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private void AssertNotAsync()
            {
                if (_isAsync)
                    throw new InvalidOperationException("Cannot use synchronous operations in asynchronous enumerator");
            }
        }
    }
}
