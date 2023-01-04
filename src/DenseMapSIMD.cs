﻿using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Intrinsics;
using Faster.Map.Core;
using System.ComponentModel;
using System.Diagnostics.Metrics;
using System.Drawing;

namespace Faster.Map
{
    /// <summary>
    /// This hashmap uses the following
    /// - Open addressing
    /// - Uses Quadratic probing 
    /// - loadfactor by default is 0.9 while maintaining an incredible speed
    /// - fibonacci hashing
    /// </summary>
    public class DenseMapSIMD<TKey, TValue>
    {
        #region Properties

        /// <summary>
        /// Gets or sets how many elements are stored in the map
        /// </summary>
        /// <value>
        /// The entry count.
        /// </value>
        public int Count { get; private set; }

        /// <summary>
        /// Gets the size of the map
        /// </summary>
        /// <value>
        /// The size.
        /// </value>
        public uint Size => (uint)_entries.Length;

        /// <summary>
        /// Returns all the entries as KeyValuePair objects
        /// </summary>
        /// <value>
        /// The entries.
        /// </value>
        public IEnumerable<KeyValuePair<TKey, TValue>> Entries
        {
            get
            {
                //iterate backwards so we can remove the item
                for (int i = _metadata.Length - 1; i >= 0; --i)
                {
                    if (_metadata[i] != _emptyBucket)
                    {
                        var entry = _entries[i];
                        yield return new KeyValuePair<TKey, TValue>(entry.Key, entry.Value);
                    }
                }
            }
        }

        /// <summary>
        /// Returns all keys
        /// </summary>
        /// <value>
        /// The keys.
        /// </value>
        public IEnumerable<TKey> Keys
        {
            get
            {
                //iterate backwards so we can remove the distance item
                for (int i = _metadata.Length - 1; i >= 0; --i)
                {
                    if (_metadata[i] != _emptyBucket)
                    {
                        yield return _entries[i].Key;
                    }
                }
            }
        }

        /// <summary>
        /// Returns all Values
        /// </summary>
        /// <value>
        /// The keys.
        /// </value>
        public IEnumerable<TValue> Values
        {
            get
            {
                for (int i = _metadata.Length - 1; i >= 0; --i)
                {
                    if (_metadata[i] != _emptyBucket)
                    {
                        yield return _entries[i].Value;
                    }
                }
            }
        }

        #endregion

        #region Fields

        private const byte _emptyBucket = 0b11111111;
        private const byte _tombstone = 0b11111110;
        private const byte num_jump_distances = 16;

        private static readonly Vector128<byte> _emptyBucketVector = Vector128.Create(_emptyBucket);
        private static readonly Vector128<byte> _deletedBucketVector = Vector128.Create(_tombstone);

        private byte[] _metadata;
        private EntrySIMD<TKey, TValue>[] _entries;

        private const uint GoldenRatio = 0x9E3779B9; //2654435769;
        private uint _length;
        private int _shift = 32;
        private uint _maxLookupsBeforeResize;
        private readonly double _loadFactor;
        private readonly IEqualityComparer<TKey> _compare;
        private const byte _bitmask = (1 << 7) - 1;

        //Probing is done by incrementing the current bucket by a triangularly increasing multiple of Groups:jump by 1 more group every time.
        //So first we jump by 1 group (meaning we just continue our linear scan), then 2 groups (skipping over 1 group), then 3 groups (skipping over 2 groups), and so on.
        //Interestingly, this pattern perfectly lines up with our power-of-two size such that we will visit every single bucket exactly once without any repeats(searching is therefore guaranteed to terminate as we always have at least one EMPTY bucket).
        //Also note that our non-linear probing strategy makes us fairly robust against weird degenerate collision chains that can make us accidentally quadratic(Hash DoS). Also also note that we expect to almost never actually probe, since that’s WIDTH(8-16) non-EMPTY buckets we need to fail to find our key in.//
        private static uint[] jump_distances = new uint[num_jump_distances]
        {
           1, 3, 6, 10, 15, 21, 28, 36, 45, 55, 66, 78, 91, 105, 120, 136
        };

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="DenseMapSIMD{TKey,TValue}"/> class.
        /// </summary>
        public DenseMapSIMD() : this(16, 0.90, EqualityComparer<TKey>.Default) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="DenseMapSIMD{TKey,TValue}"/> class.
        /// </summary>
        /// <param name="length">The length of the hashmap. Will always take the closest power of two</param>
        public DenseMapSIMD(uint length) : this(length, 0.90, EqualityComparer<TKey>.Default) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="DenseMapSIMD{TKey,TValue}"/> class.
        /// </summary>
        /// <param name="length">The length of the hashmap. Will always take the closest power of two</param>
        /// <param name="loadFactor">The loadfactor determines when the hashmap will resize(default is 0.9d)</param>
        public DenseMapSIMD(uint length, double loadFactor) : this(length, loadFactor, EqualityComparer<TKey>.Default) { }

        /// <summary>
        /// Initializes a new instance of class.
        /// </summary>
        /// <param name="length">The length of the hashmap. Will always take the closest power of two</param>
        /// <param name="loadFactor">The loadfactor determines when the hashmap will resize(default is 0.9d)</param>
        /// <param name="keyComparer">Used to compare keys to resolve hashcollisions</param>
        public DenseMapSIMD(uint length, double loadFactor, IEqualityComparer<TKey> keyComparer)
        {
            if (!Sse2.IsSupported)
            {
                throw new NotSupportedException("Simd SSe2 is not supported");
            }

            //default length is 16
            _length = length;
            _loadFactor = loadFactor;

            if (BitOperations.IsPow2(length))
            {
                _length = length;
            }
            else
            {
                _length = BitOperations.RoundUpToPowerOf2(_length);
            }

            _maxLookupsBeforeResize = (uint)(_length * loadFactor);
            _compare = keyComparer ?? EqualityComparer<TKey>.Default;

            _shift = _shift - Log2(_length) + 1;

            _entries = new EntrySIMD<TKey, TValue>[_length + 16];

            _metadata = new byte[_length + 16];
            //fill metadata with emptybucket info
            Array.Fill(_metadata, _emptyBucket);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Insert a key and value in the hashmap
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <returns>returns false if key alreadyt exists</returns>
        [MethodImpl(256)]
        public bool Emplace(TKey key, TValue value)
        {
            //Resize if loadfactor is reached
            if (Count >= _maxLookupsBeforeResize)
            {
                Resize();
            }

            // get object identity hashcode
            var hashcode = key.GetHashCode();

            // Objectidentity hashcode * golden ratio (fibonnachi hashing) followed by a shift
            uint index = (uint)hashcode * GoldenRatio >> _shift;

            // get 7 high bits from hashcode
            byte h2 = (byte)(hashcode & _bitmask);

            //  check if key is unique
            if (ContainsKey(ref h2, index, key))
            {
                return false;
            }

            //create entry
            EntrySIMD<TKey, TValue> entry = default;
            entry.Value = value;
            entry.Key = key;

            byte distance = 0;
            uint jumpDistance = 0;

            while (true)
            {
                var right = Vector128.LoadUnsafe(ref _metadata[index], jumpDistance);
                var tombstones = Sse2.CompareEqual(_deletedBucketVector, right);

                //check for tombstones - deleted entries
                int result = Sse2.MoveMask(tombstones);
                if (result != 0)
                {
                    index += jumpDistance + (uint)BitOperations.TrailingZeroCount(result);

                    _entries[index] = entry;
                    _metadata[index] = h2;

                    ++Count;
                    return true;
                }

                var _emptyBuckets = Sse2.CompareEqual(_emptyBucketVector, right);

                //check for empty entries
                result = Sse2.MoveMask(_emptyBuckets);
                if (result != 0)
                {
                    index += jumpDistance + (uint)BitOperations.TrailingZeroCount(result);

                    _entries[index] = entry;
                    _metadata[index] = h2;

                    ++Count;
                    return true;
                }

                //calculate jump distance
                jumpDistance = 16 * jump_distances[distance];

                distance++;

                if (index + jumpDistance > _length)
                {
                    Resize();
                    EmplaceInternal(ref entry, ref h2);
                    ++Count;
                    return true;
                }
            }
        }

        /// <summary>
        /// Gets the value with the corresponding key
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <returns></returns>       
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Get(TKey key, out TValue value)
        {
            //Get object identity hashcode
            var hashcode = key.GetHashCode();

            // Objectidentity hashcode * golden ratio (fibonnachi hashing) followed by a shift
            uint index = (uint)hashcode * GoldenRatio >> _shift;

            //create vector of the bottom 7 bits
            var left = Vector128.Create((byte)(hashcode & _bitmask));

            byte distance = 0;
            uint jumpDistance = 0;

            while (true)
            {
                //load vector @ index
                var right = Vector128.LoadUnsafe(ref _metadata[index], jumpDistance);

                //compare two vectors
                var comparison = Sse2.CompareEqual(left, right);

                //get result
                int result = Sse2.MoveMask(comparison);

                //Could be multiple bits which are set
                while (result != 0)
                {
                    //retrieve offset 
                    var offset = BitOperations.TrailingZeroCount(result);

                    //get index and eq
                    var entry = _entries[index + jumpDistance + offset];

                    if (_compare.Equals(entry.Key, key))
                    {
                        value = entry.Value;
                        return true;
                    }

                    //clear bit
                    result &= ~(1 << offset);
                }

                result = Sse2.MoveMask(Sse2.CompareEqual(_emptyBucketVector, right));
                if (result != 0)
                {
                    //contains empty buckets - break;

                    value = default;

                    //not found
                    return false;
                }

                //calculate jump distance
                jumpDistance = 16 * jump_distances[distance];

                distance++;

                if (index + jumpDistance > _length + 16)
                {
                    value = default;
                    return false;
                }
            }
        }

        /// <summary>
        /// Updates the value of a specific key
        /// </summary>
        /// <param name="key"></param>
        /// <param name="value"></param>
        /// <returns> returns if update succeeded or not</returns>
        [MethodImpl(256)]
        public bool Update(TKey key, TValue value)
        {
            //Get object identity hashcode
            var hashcode = key.GetHashCode();

            // Objectidentity hashcode * golden ratio (fibonnachi hashing) followed by a shift
            uint index = (uint)hashcode * GoldenRatio >> _shift;

            //create vector of lower first 7 bits
            var left = Vector128.Create((byte)(hashcode & _bitmask));

            byte distance = 0;
            uint jumpDistance = 0;

            while (true)
            {
                var right = Vector128.LoadUnsafe(ref _metadata[index], jumpDistance);
                var comparison = Sse2.CompareEqual(left, right);

                //get result
                int result = Sse2.MoveMask(comparison);

                //Could be multiple bits which are set
                while (result != 0)
                {
                    //retrieve offset 
                    var offset = BitOperations.TrailingZeroCount(result);

                    //get index and eq
                    ref var entry = ref _entries[index + jumpDistance + offset];

                    if (_compare.Equals(entry.Key, key))
                    {
                        entry.Value = value;
                        return true;
                    }

                    //clear bit
                    result &= ~(1 << offset);
                }

                comparison = Sse2.CompareEqual(_emptyBucketVector, right);
                result = Sse2.MoveMask(comparison);

                if (result != 0)
                {
                    //contains empty buckets - break;
                    break;
                }

                //calculate jump distance
                jumpDistance = 16 * jump_distances[distance];

                distance++;

                if (index + jumpDistance > _length)
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Removes a key and value from the map
        /// </summary>
        /// <param name="key"></param>
        /// <returns> returns if the removal succeeded </returns>
        [MethodImpl(256)]
        public bool Remove(TKey key)
        {
            //Get object identity hashcode
            var hashcode = key.GetHashCode();

            // Objectidentity hashcode * golden ratio (fibonnachi hashing) followed by a shift
            uint index = (uint)hashcode * GoldenRatio >> _shift;

            //get lower first 7 bits

            var left = Vector128.Create((byte)(hashcode & _bitmask));

            byte distance = 0;
            uint jumpDistance = 0;

            while (true)
            {
                var right = Vector128.LoadUnsafe(ref _metadata[index], jumpDistance);
                var comparison = Sse2.CompareEqual(left, right);

                //get result
                var result = Sse2.MoveMask(comparison);

                //Could be multiple bits which are set
                while (result != 0)
                {
                    //retrieve offset 
                    var offset = BitOperations.TrailingZeroCount(result);

                    //get index and eq
                    var entry = _entries[index + jumpDistance + offset];

                    if (_compare.Equals(entry.Key, key))
                    {
                        _entries[index + jumpDistance + offset] = default;
                        _metadata[index + jumpDistance + offset] = _tombstone;
                        --Count;
                        return true;
                    }

                    //clear bit
                    result &= ~(1 << offset);
                }

                //find an empty spot, which means the key is not found
                comparison = Sse2.CompareEqual(_emptyBucketVector, right);
                result = Sse2.MoveMask(comparison);

                if (result != 0)
                {
                    //contains empty buckets - break;
                    break;
                }

                //calculate jump distance

                jumpDistance = 16 * jump_distances[distance];

                distance++;

                if (index + jumpDistance > _length)
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// determines if hashmap contains key x
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns> returns if a key is found </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(TKey key)
        {
            //Get object identity hashcode
            var hashcode = key.GetHashCode();

            // Objectidentity hashcode * golden ratio (fibonnachi hashing) followed by a shift
            uint index = (uint)hashcode * GoldenRatio >> _shift;

            //create vector of the bottom 7 bits
            var left = Vector128.Create((byte)(hashcode & _bitmask));

            byte distance = 0;
            uint jumpDistance = 0;

            while (true)
            {
                //load vector @ index
                var right = Vector128.LoadUnsafe(ref _metadata[index], jumpDistance);

                //compare two vectors
                var comparison = Sse2.CompareEqual(left, right);

                //get result
                int result = Sse2.MoveMask(comparison);

                //Could be multiple bits which are set
                while (result != 0)
                {
                    //retrieve offset 
                    var offset = BitOperations.TrailingZeroCount(result);

                    //get index and eq
                    var entry = _entries[index + jumpDistance + offset];

                    if (_compare.Equals(entry.Key, key))
                    {
                        return true;
                    }

                    //clear bit
                    result &= ~(1 << offset);
                }

                result = Sse2.MoveMask(Sse2.CompareEqual(_emptyBucketVector, right));
                if (result != 0)
                {
                    //contains empty buckets - break;  
                    return false;
                }

                //calculate jump distance
                jumpDistance = 16 * jump_distances[distance];

                distance++;

                if (index + jumpDistance > _length)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Copies entries from one map to another
        /// </summary>
        /// <param name="denseMap">The map.</param>
        public void Copy(DenseMapSIMD<TKey, TValue> denseMap)
        {
            for (var i = 0; i < denseMap._entries.Length; ++i)
            {
                var metadata = denseMap._metadata[i];
                if (metadata == _emptyBucket)
                {
                    continue;
                }

                var entry = denseMap._entries[i];
                Emplace(entry.Key, entry.Value);
            }
        }

        /// <summary>
        /// Clears this instance.
        /// </summary>
        public void Clear()
        {
            Array.Clear(_entries);
            Array.Fill(_metadata, _emptyBucket);

            Count = 0;
        }

        /// <summary>
        /// Gets or sets the value by using a Tkey
        /// </summary>
        /// <value>
        /// The 
        /// </value>
        /// <param name="key">The key.</param>
        /// <returns></returns>
        /// <exception cref="KeyNotFoundException">
        /// Unable to find entry - {key.GetType().FullName} key - {key.GetHashCode()}
        /// or
        /// Unable to find entry - {key.GetType().FullName} key - {key.GetHashCode()}
        /// </exception>
        public TValue this[TKey key]
        {
            get
            {
                if (Get(key, out var result))
                {
                    return result;
                }

                throw new KeyNotFoundException($"Unable to find entry - {key.GetType().FullName} key - {key.GetHashCode()}");
            }
            set
            {
                if (!Update(key, value))
                {
                    throw new KeyNotFoundException($"Unable to find entry - {key.GetType().FullName} key - {key.GetHashCode()}");
                }
            }
        }

        /// <summary>
        /// Returns an index of the specified key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns></returns>
        public int IndexOf(TKey key)
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                if (_metadata[i] == _emptyBucket)
                {
                    continue;
                }

                var entry = _entries[i];
                if (_compare.Equals(key, entry.Key))
                {
                    return i;
                }
            }
            return -1;
        }

        #endregion

        #region Private Methods

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool ContainsKey(ref byte h2, uint index, TKey key)
        {
            //create vector with partial hash h2
            var left = Vector128.Create(h2);

            //default jump distance             
            byte distance = 0;

            uint jumpDistance = 0;

            while (true)
            {
                //load metadata unsafe
                var right = Vector128.LoadUnsafe(ref _metadata[index], jumpDistance);

                //compare vectors
                var comparison = Sse2.CompareEqual(left, right);

                //convert to int bitarray
                int result = Sse2.MoveMask(comparison);

                //Could be multiple bits which are set
                while (result != 0)
                {
                    //retrieve offset 
                    var offset = BitOperations.TrailingZeroCount(result);

                    //get index and eq
                    var entry = _entries[index + jumpDistance + offset];

                    if (_compare.Equals(entry.Key, key))
                    {
                        return true;
                    }

                    //clear bit
                    result &= ~(1 << offset);
                }

                //search for empty buckets
                comparison = Sse2.CompareEqual(_emptyBucketVector, right);
                result = Sse2.MoveMask(comparison);

                if (result != 0)
                {
                    //contains empty buckets - break;
                    return false;
                }

                //calculate jump distance
                jumpDistance = 16 * jump_distances[distance];

                distance++;

                if (index + jumpDistance > _length)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Emplaces a new entry without checking for key existence. Keys have already been checked and are unique
        /// </summary>
        /// <param name="entry">The entry.</param>
        /// <param name="current">The distance.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EmplaceInternal(ref EntrySIMD<TKey, TValue> entry, ref byte h2)
        {
            //expensive if hashcode is slow, or when it`s not cached like strings
            var hashcode = entry.Key.GetHashCode();

            //calculatge index by using obhect identity * fibonaci followed by a shift
            uint index = (uint)hashcode * GoldenRatio >> _shift;

            byte distance = 0;
            uint jumpDistance = 0;

            while (true)
            {
                var right = Vector128.LoadUnsafe(ref _metadata[index], jumpDistance);
                var tombstones = Sse2.CompareEqual(_deletedBucketVector, right);

                //check for tombstones - deleted entries
                int result = Sse2.MoveMask(tombstones);
                if (result != 0)
                {
                    index += jumpDistance + (uint)BitOperations.TrailingZeroCount(result);

                    _entries[index] = entry;
                    _metadata[index] = h2;

                    return;
                }

                var _emptyBuckets = Sse2.CompareEqual(_emptyBucketVector, right);

                //check for empty entries
                result = Sse2.MoveMask(_emptyBuckets);
                if (result != 0)
                {
                    index += jumpDistance + (uint)BitOperations.TrailingZeroCount(result);

                    _entries[index] = entry;
                    _metadata[index] = h2;
                    return;
                }

                //calculate jump distance          
                jumpDistance = 16 * jump_distances[distance];

                distance++;

                if (index + jumpDistance > _length)
                {
                    Resize();
                    EmplaceInternal(ref entry, ref h2);
                    return;
                }
            }
        }

        /// <summary>
        /// Resizes this instance.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Resize()
        {
            _shift--;

            //next pow of 2
            _length = _length * 2;

            _maxLookupsBeforeResize = (uint)(_length * _loadFactor);

            var oldEntries = new EntrySIMD<TKey, TValue>[_entries.Length];
            Array.Copy(_entries, oldEntries, _entries.Length);

            var oldMetadata = new byte[_metadata.Length];
            Array.Copy(_metadata, oldMetadata, _metadata.Length);

            _metadata = new byte[_length + 16];

            Array.Fill(_metadata, _emptyBucket);

            _entries = new EntrySIMD<TKey, TValue>[_length + 16];

            for (var i = 0; i < oldEntries.Length; ++i)
            {
                var m = oldMetadata[i];
                if (m == _emptyBucket || m == _tombstone)
                {
                    continue;
                }

                var entry = oldEntries[i];

                EmplaceInternal(ref entry, ref m);
            }
        }

        // used for set checking operations (using enumerables) that rely on counting
        private static byte Log2(uint value)
        {
            byte c = 0;
            while (value > 0)
            {
                c++;
                value >>= 1;
            }

            return c;
        }

        #endregion
    }
}

