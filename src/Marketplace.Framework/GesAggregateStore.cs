﻿using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EventStore.ClientAPI;
using EventStore.ClientAPI.Exceptions;
using EventStore.ClientAPI.SystemData;
using Marketplace.Framework.Logging;

namespace Marketplace.Framework
{
    using static String;

    public class GesAggregateStore : IAggregateStore
    {
        private const int MaxReadSize = 4096;

        private static readonly ILog Log = LogProvider.For<GesAggregateStore>();
        private readonly IEventStoreConnection _connection;

        private readonly GetStreamName _getStreamName;
        private readonly ISerializer _serializer;
        private readonly TypeMapper _typeMapper;
        private readonly UserCredentials _userCredentials;

        public GesAggregateStore(
            GetStreamName getStreamName,
            IEventStoreConnection connection,
            ISerializer serializer,
            TypeMapper typeMapper,
            UserCredentials userCredentials = null)
        {
            _getStreamName = getStreamName ?? throw new ArgumentNullException(nameof(connection));
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _typeMapper = typeMapper ?? throw new ArgumentNullException(nameof(typeMapper));
            _userCredentials = userCredentials;
        }

        /// <summary>
        ///     Loads and returns an aggregate by id, from the store.
        /// </summary>
        public async Task<T> Load<T>(string aggregateId, CancellationToken cancellationToken = default)
            where T : Aggregate, new()
        {
            if (IsNullOrWhiteSpace(aggregateId))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(aggregateId));

            var stream = _getStreamName(typeof(T), aggregateId);
            var aggregate = new T();

            var nextPageStart = 0L;
            do
            {
                var page = await _connection.ReadStreamEventsForwardAsync(
                    stream, nextPageStart, MaxReadSize, false, _userCredentials);

                aggregate.LoadFromHistory(page.Events.Select(resolvedEvent =>
                {
                    var dataType = _typeMapper.GetType(resolvedEvent.Event.EventType);
                    var data = _serializer.Deserialize(resolvedEvent.Event.Data, dataType);
                    return data;
                }).ToArray());

                nextPageStart = !page.IsEndOfStream ? page.NextEventNumber : -1;
            } while (nextPageStart != -1);

            Log.Info("Loaded {aggregate} changes from stream {stream}", aggregate, stream);

            return aggregate;
        }

        /// <summary>
        ///     Saves changes to the store.
        /// </summary>
        public async Task<(long NextExpectedVersion, long LogPosition, long CommitPosition )> Save<T>(
            T aggregate, Metadata metadata = null, CancellationToken cancellationToken = default)
            where T : Aggregate
        {
            if (aggregate == null)
                throw new ArgumentNullException(nameof(aggregate));

            var changes = aggregate.GetChanges()
                .Select((e, idx) =>
                {
                    var eventMetadata = new Metadata(metadata).AddRange(
                        (MetadataKeys.EventSource, typeof(T).FullName),
                        (MetadataKeys.EventSourceId, aggregate.Id),
                        (MetadataKeys.EventSourceVersion, (aggregate.Version + idx + 1).ToString()),
                        (MetadataKeys.ClrType, e.GetType().ToString())
                    );
                    return new EventData(
                        Guid.NewGuid(),
                        _typeMapper.GetTypeName(e.GetType()),
                        _serializer.IsJsonSerializer,
                        _serializer.Serialize(e),
                        _serializer.Serialize(eventMetadata));
                })
                .ToArray();

            if (!changes.Any())
            {
                Log.Warn("{Id} v{Version} aggregate has no changes.", aggregate.Id, aggregate.Version);
                return default;
            }

            var stream = _getStreamName(typeof(T), aggregate.Id);

            WriteResult result;
            try
            {
                result = await _connection.AppendToStreamAsync(stream, aggregate.Version, changes, _userCredentials);
            }
            catch (WrongExpectedVersionException)
            {
                var page = await _connection.ReadStreamEventsBackwardAsync(stream, -1, 1, false, _userCredentials);
                throw new WrongExpectedStreamVersionException(
                    $"Failed to append stream {stream} with expected version {aggregate.Version}. " +
                    $"{(page.Status == SliceReadStatus.StreamNotFound ? "Stream not found!" : $"Current Version: {page.LastEventNumber}")}");
            }

            Log.Info("Saved {aggregate} changes into stream {streamName}", aggregate, stream);

            return (
                result.NextExpectedVersion,
                result.LogPosition.CommitPosition,
                result.LogPosition.PreparePosition);
        }

        /// <summary>
        ///     Returns the last version of the aggregate, if found.
        /// </summary>
        public async Task<long> GetLastVersionOf<T>(string aggregateId, CancellationToken cancellationToken = default)
            where T : Aggregate
        {
            if (IsNullOrWhiteSpace(aggregateId))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(aggregateId));

            var page = await _connection.ReadStreamEventsBackwardAsync(
                _getStreamName(typeof(T), aggregateId), long.MaxValue, 1, false, _userCredentials);

            return page.LastEventNumber;
        }
    }


//
//    public class GesEventStore : IAggregateStore
//    {
//        private const int MaxReadSize = 4096;
//        
//        private static readonly ILog Log = LogProvider.For<GesAggregateStore>();
//
//        private readonly GetStreamName         _getStreamName;
//        private readonly IEventStoreConnection _connection;
//        private readonly ISerializer           _serializer;
//        private readonly TypeMapper            _typeMapper;
//        private readonly UserCredentials       _userCredentials;
//
//        public GesAggregateStore(
//            GetStreamName getStreamName,
//            IEventStoreConnection connection,
//            ISerializer serializer,
//            TypeMapper typeMapper,
//            UserCredentials userCredentials = null)
//        {
//            _getStreamName   = getStreamName ?? throw new ArgumentNullException(nameof(connection));
//            _connection      = connection    ?? throw new ArgumentNullException(nameof(connection));
//            _serializer      = serializer    ?? throw new ArgumentNullException(nameof(serializer));
//            _typeMapper      = typeMapper    ?? throw new ArgumentNullException(nameof(typeMapper));
//            _userCredentials = userCredentials;
//        }
//
//        /// <summary>
//        ///     Loads and returns an aggregate by id, from the store.
//        /// </summary>
//        public async Task<T> Load<T>(string aggregateId, CancellationToken cancellationToken = default) where T : Aggregate, new()
//        {
//            if (IsNullOrWhiteSpace(aggregateId))
//                throw new ArgumentException("Value cannot be null or whitespace.", nameof(aggregateId));
//
//            var stream    = _getStreamName(typeof(T), aggregateId);
//            var aggregate = new T();
//            
//            var nextPageStart = 0L;
//            do {
//                var page = await _connection.ReadStreamEventsForwardAsync(
//                    stream, nextPageStart, MaxReadSize, false, _userCredentials);
//                
//                aggregate.LoadFromHistory(page.Events.Select(resolvedEvent => {
//                    var dataType = _typeMapper.GetType(resolvedEvent.Event.EventType);
//                    var data     = _serializer.Deserialize(resolvedEvent.Event.Data, dataType);
//                    return data;
//                }).ToArray());
//                
//                nextPageStart = !page.IsEndOfStream ? page.NextEventNumber : -1;
//            } 
//            while (nextPageStart != -1);
//
//            Log.Info("Loaded {aggregate} changes from stream {stream}", aggregate, stream);
//
//            return aggregate;
//        }
//        
//        /// <summary>
//        ///     Saves changes to the store.
//        /// </summary>
//        public async Task<(long NextExpectedVersion, long LogPosition, long CommitPosition )> Save<T>(
//            T aggregate, Metadata metadata = null, CancellationToken cancellationToken = default)
//            where T : Aggregate
//        {
//            if (aggregate == null)
//                throw new ArgumentNullException(nameof(aggregate));
//
//            var changes = aggregate.GetChanges()
//                                   .Select((e, idx) => {
//                                       var eventMetadata = new Metadata(metadata).AddRange(
//                                           (MetadataKeys.EventSource       , typeof(T).FullName),
//                                           (MetadataKeys.EventSourceId     , aggregate.Id),
//                                           (MetadataKeys.EventSourceVersion, (aggregate.Version + idx + 1).ToString()),
//                                           (MetadataKeys.ClrType           , e.GetType().ToString())
//                                       );
//                                       return new EventData(
//                                           Guid.NewGuid(),
//                                           _typeMapper.GetTypeName(e.GetType()),
//                                           _serializer.IsJsonSerializer,
//                                           _serializer.Serialize(e),
//                                           _serializer.Serialize(eventMetadata));
//                                   })
//                                   .ToArray();
//            
//            if (!changes.Any()) {
//                Log.Warn("{Id} v{Version} aggregate has no changes.", aggregate.Id, aggregate.Version);
//                return default;
//            }          
//
//            var stream = _getStreamName(typeof(T), aggregate.Id);
//
//            WriteResult result;
//            try {
//                 result = await _connection.AppendToStreamAsync(stream, aggregate.Version, changes, _userCredentials);
//            }
//            catch (WrongExpectedVersionException) {
//                var page = await _connection.ReadStreamEventsBackwardAsync(stream, -1, 1, false, _userCredentials);
//                throw new WrongExpectedStreamVersionException(
//                    $"Failed to append stream {stream} with expected version {aggregate.Version}. " +
//                    $"{(page.Status == SliceReadStatus.StreamNotFound ? "Stream not found!" : $"Current Version: {page.LastEventNumber}")}");
//            }
//
//            Log.Info("Saved {aggregate} changes into stream {streamName}", aggregate, stream);
//
//            return (
//                result.NextExpectedVersion, 
//                result.LogPosition.CommitPosition, 
//                result.LogPosition.PreparePosition);
//        }
//
//        /// <summary>
//        ///     Returns the last version of the aggregate, if found.
//        /// </summary>
//        public async Task<long> GetLastVersionOf<T>(string aggregateId, CancellationToken cancellationToken = default) where T : Aggregate {
//            if (IsNullOrWhiteSpace(aggregateId))
//                throw new ArgumentException("Value cannot be null or whitespace.", nameof(aggregateId));
//
//            var page = await _connection.ReadStreamEventsBackwardAsync(
//                _getStreamName(typeof(T), aggregateId), long.MaxValue, 1, false, _userCredentials);
//
//            return page.LastEventNumber;
//        }
//    }
//    
//    public enum StreamReadDirection
//    {
//        /// <summary>
//        /// From beginning to end.
//        /// </summary>
//        Forward,
//        /// <summary>
//        /// From end to beginning.
//        /// </summary>
//        Backward
//    }
//    
//    public interface IEventStore
//    {
//        Task AppendStream(
//            string stream,
//            long expectedVersion,
//            object[] events,
//            CancellationToken cancellationToken = default);
//
//        Task<object[]> ReadStream(
//            string stream,
//            long fromVersion,
//            int pageSize,
//            StreamReadDirection direction = StreamReadDirection.Forward,
//            CancellationToken cancellationToken = default);
//     }
//
//    public class AggregateStore : IAggregateStore
//    {
//        private readonly IEventStore _eventStore;
//        private readonly GetStreamName _getStreamName;
//        
//        public AggregateStore(GetStreamName getStreamName, IEventStore eventStore)
//        {
//            _eventStore = eventStore;
//        }
//        
//        public Task<T> Load<T>(string aggregateId, CancellationToken cancellationToken = default) where T : Aggregate, new()
//        {
//            if (IsNullOrWhiteSpace(aggregateId))
//                throw new ArgumentException("Value cannot be null or whitespace.", nameof(aggregateId));
//
//            var stream    = _getStreamName(typeof(T), aggregateId);
//            var aggregate = new T();
//            
//          
//          
//                var page = await _eventStore.ReadStream(
//                    stream, 0, MaxReadSize, false, _userCredentials);
//                
//                aggregate.LoadFromHistory(page.Events.Select(resolvedEvent => {
//                    var dataType = _typeMapper.GetType(resolvedEvent.Event.EventType);
//                    var data     = _serializer.Deserialize(resolvedEvent.Event.Data, dataType);
//                    return data;
//                }).ToArray());
//                
//                nextPageStart = !page.IsEndOfStream ? page.NextEventNumber : -1;
//            } 
//            while (nextPageStart != -1);
//
//            Log.Info("Loaded {aggregate} changes from stream {stream}", aggregate, stream);
//
//            return aggregate;
//        }
//
//        public Task<(long NextExpectedVersion, long LogPosition, long CommitPosition)> Save<T>(T aggregate, Metadata metadata = null, CancellationToken cancellationToken = default) where T : Aggregate
//        {
//            throw new NotImplementedException();
//        }
//
//        public Task<long> GetLastVersionOf<T>(string aggregateId, CancellationToken cancellationToken = default) where T : Aggregate
//        {
//            throw new NotImplementedException();
//        }
//    }
}