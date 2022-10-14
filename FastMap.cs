﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Faster.Map.Core;

namespace Faster.Map
{
    /// <summary>
    /// This hashmap is heavily optimized to be used with numerical keys and  uses the following
    /// - Open addressing
    /// - Uses linear probing
    /// - Robinhood hashmap
    /// - Upper limit on the probe sequence lenght(psl) which is Log2(size)
    /// - Fibonacci hashing
    /// - Only usable with valuetype keys
    /// </summary>
    /// <typeparam name="TKey">The type of the key.</typeparam>
    /// <typeparam name="TValue">The type of the value.</typeparam>
    public class FastMap<TKey, TValue> where TKey : unmanaged
    {
        #region Properties

        /// <summary>
        /// Gets or sets how many elements are stored in the map
        /// </summary>
        /// <value>
        /// The entry count.
        /// </value>
        public uint Count { get; private set; }

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
                //iterate backwards so we can remove the current item
                for (int i = _info.Length - 1; i >= 0; --i)
                {
                    if (!_info[i].IsEmpty())
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
                //iterate backwards so we can remove the current item
                for (int i = _info.Length - 1; i >= 0; --i)
                {
                    if (!_info[i].IsEmpty())
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
                for (int i = _info.Length - 1; i >= 0; --i)
                {
                    if (!_info[i].IsEmpty())
                    {
                        yield return _entries[i].Value;
                    }
                }
            }
        }

        #endregion

        #region Fields

        private InfoByte[] _info;
        private FastEntry<TKey, TValue>[] _entries;
        private uint _maxlookups;
        private readonly double _loadFactor;
        private const uint GoldenRatio = 0x9E3779B9; //2654435769;
        private int _shift = 32;
        private byte _maxProbeSequenceLength;
        private byte _currentProbeSequenceLength;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="Map{TKey,TValue}"/> class.
        /// </summary>
        public FastMap() : this(8, 0.5d) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="Map{TKey, TValue}"/> class.
        /// </summary>
        /// <param name="length">The length of the hashmap. Will always take the closest power of two</param>
        public FastMap(uint length) : this(length, 0.5d) { }

        /// <summary>
        /// Initializes a new instance of class.
        /// </summary>
        /// <param name="length">The length of the hashmap. Will always take the closest power of two</param>
        /// <param name="loadFactor">The loadfactor determines when the hashmap will resize(default is 0.5d) i.e size 32 loadfactor 0.5 hashmap will resize at 16</param>
        public FastMap(uint length, double loadFactor)
        {
            //default length is 16
            _maxlookups = length == 0 ? 8 : length;
            _loadFactor = loadFactor;

            var size = NextPow2(_maxlookups);
            _maxProbeSequenceLength = loadFactor <= 0.5 ? Log2(size) : PslLimit(size);

            _shift = _shift - Log2(_maxlookups) + 1;
            _entries = new FastEntry<TKey, TValue>[size + _maxProbeSequenceLength + 1];
            _info = new InfoByte[size + _maxProbeSequenceLength + 1];
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Inserts a value using a key.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <returns></returns>
        [MethodImpl(256)]
        public bool Emplace(TKey key, TValue value)
        {
            if ((double)Count / _maxlookups > _loadFactor)
            {
                Resize();
            }

            var hashcode = key.GetHashCode();
            uint index = (uint)hashcode * GoldenRatio >> _shift;
            
            FastEntry<TKey, TValue> insertableFastEntry = default;
            insertableFastEntry.Value = value;
            insertableFastEntry.Key = key;

            InfoByte insertableInfo = default;
            insertableInfo.Psl = 0;
            
            var info = _info[index];
            if (info.IsEmpty())
            {
                _entries[index] = insertableFastEntry;
                _info[index] = insertableInfo;
                ++Count;
                return true;
            }

            ++index;

            if (Contains(key))
            {
                return false;
            }

            for (; ; ++insertableInfo.Psl, ++index)
            {
                if (_currentProbeSequenceLength < insertableInfo.Psl)
                {
                    _currentProbeSequenceLength = insertableInfo.Psl;
                }

                info = _info[index];
                if (info.IsEmpty())
                {
                    _entries[index] = insertableFastEntry;
                    _info[index] = insertableInfo;
                    ++Count;
                    return true;
                }

                if (insertableInfo.Psl > info.Psl)
                {
                    Swap(ref insertableFastEntry, ref _entries[index]);
                    Swap(ref insertableInfo, ref _info[index]);
                    continue;
                }

                if (insertableInfo.Psl == _maxProbeSequenceLength)
                {
                    ++Count;
                    IncreaseMaxProbeSequence();
                    Resize();
                    EmplaceInternal(ref insertableFastEntry, ref insertableInfo);
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
        [MethodImpl(256)]
        public bool Get(TKey key, out TValue value)
        {
            //Get object identity hashcode
            var hashcode = key.GetHashCode();

            // Objectidentity hashcode * golden ratio (fibonnachi hashing) followed by a shift
            uint index = (uint)hashcode * GoldenRatio >> _shift;

            //Determine max distance
            var maxDistance = index + _currentProbeSequenceLength;

            do
            {
                //unrolling loop twice seems to give a minor speedboost
                var entry = _entries[index];

                //validate hashcode
                if (hashcode == entry.Key.GetHashCode())
                {
                    value = entry.Value;
                    return true;
                }

                //increase index by 1
                entry = _entries[++index];

                //validate hashcode
                if (hashcode == entry.Key.GetHashCode())
                {
                    value = entry.Value;
                    return true;
                }

                //increase index by one and validate if within bounds
            } while (++index <= maxDistance);


            value = default;
            return false;
        }

        /// <summary>
        /// Update the entry by using a key and value
        /// </summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The value.</param>
        /// <returns></returns>
        [MethodImpl(256)]
        public bool Update(TKey key, TValue value)
        {
            //Get object identity hashcode
            var hashcode = key.GetHashCode();

            //Objectidentity hashcode * golden ratio (fibonnachi hashing) followed by a shift
            uint index = (uint)hashcode * GoldenRatio >> _shift;

            //Determine max distance
            var maxDistance = index + _currentProbeSequenceLength;

            do
            {
                //unrolling loop twice seems to give a minor speedboost
                var entry = _entries[index];

                //validate hashcode
                if (hashcode == entry.Key.GetHashCode())
                {
                    _entries[index].Value = value;
                    return true;
                }

                //increase index by 1
                entry = _entries[++index];

                //validate hashcode
                if (hashcode == entry.Key.GetHashCode())
                {
                    _entries[index].Value = value;
                    return true;
                }

                //increase index by one and validate if within bounds
            } while (++index <= maxDistance);
            
            //entry not found
            return false;
        }

        /// <summary>
        /// Removes entry using a backshift removal
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>Operation succeeded yes or no</returns>
        [MethodImpl(256)]
        public bool Remove(TKey key)
        {
            //Get ObjectIdentity hashcode
            int hashcode = key.GetHashCode();

            //Objectidentity hashcode * golden ratio (fibonnachi hashing) followed by a shift
            uint index = (uint)hashcode * GoldenRatio >> _shift;

            //Determine max distance
            var maxDistance = index + _currentProbeSequenceLength;

            do
            {
                //unrolling loop twice seems to give a minor speedboost
                var entry = _entries[index];

                //validate hash
                if (hashcode == entry.Key.GetHashCode())
                {
                    //remove entry from list
                    _entries[index] = default;
                    _info[index] = default;
                    --Count;
                    ShiftRemove(index);
                    return true;
                }

                //increase index by 1
                entry = _entries[++index];

                //validate hash
                if (hashcode == entry.Key.GetHashCode())
                {
                    //remove entry from list
                    _entries[index] = default;
                    _info[index] = default;
                    --Count;
                    ShiftRemove(index);
                    return true;
                }

                //increase index by one and validate if within bounds
            } while (++index <= maxDistance);

            // No entries removed
            return false;
        }

        /// <summary>
        /// Determines whether the specified key exists in the hashmap
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>
        ///   <c>true</c> if the specified key contains key; otherwise, <c>false</c>.
        /// </returns>
        [MethodImpl(256)]
        public bool Contains(TKey key)
        {
            //Get ObjectIdentity hashcode
            int hashcode = key.GetHashCode();

            //Objectidentity hashcode * golden ratio (fibonnachi hashing) followed by a shift
            uint index = (uint)hashcode * GoldenRatio >> _shift;

            //Determine max distance
            var maxDistance = index + _currentProbeSequenceLength;

            do
            {
                //unrolling loop twice seems to give a minor speedboost
                var entry = _entries[index];

                //validate hash
                if (hashcode == entry.Key.GetHashCode())
                {
                    return true;
                }

                //increase index by 1
                entry = _entries[++index];

                //validate hash
                if (hashcode == entry.Key.GetHashCode())
                {
                    return true;
                }

                //increase index by one and validate if within bounds
            } while (++index <= maxDistance);


            return false;
        }

        /// <summary>
        /// Copies all entries from source to target
        /// </summary>
        /// <param name="fastMap">The fast map.</param>
        [MethodImpl(256)]
        public void Copy(FastMap<TKey, TValue> fastMap)
        {
            for (var i = 0; i < fastMap._entries.Length; i++)
            {
                var info = fastMap._info[i];
                if (info.IsEmpty())
                {
                    continue;
                }

                var entry = _entries[i];
                EmplaceInternal(ref entry, ref info);
            }
        }

        /// <summary>
        /// Returns the current index of Tkey
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns></returns>
        [MethodImpl(256)]
        public int IndexOf(TKey key)
        {
            var hashcode = key.GetHashCode();
            for (int i = 0; i < _entries.Length; i++)
            {
                var info = _info[i];
                if (info.IsEmpty())
                {
                    continue;
                }

                if (_entries[i].Key.GetHashCode() == hashcode)
                {
                    return i;
                }
            }

            //Return -1 which indicates not found
            return -1;
        }

        /// <summary>
        /// Set default state of all entries
        /// </summary>
        public void Clear()
        {
            for (var i = 0; i < _entries.Length; i++)
            {
                _info[i] = default;
                _entries[i] = default;
            }

            Count = 0;
        }

        /// <summary>
        /// Gets or sets the entry by using a TKey
        /// </summary>
        /// <value>
        /// </value>
        /// <param name="key">The key.</param>
        /// <returns></returns>
        /// <exception cref="System.Collections.Generic.KeyNotFoundException">Unable to find entry - {key.GetType().FullName} key - {key.GetHashCode()}
        /// or
        /// Unable to find entry - {key.GetType().FullName} key - {key.GetHashCode()}</exception>
        /// <exception cref="KeyNotFoundException">Unable to find entry - {key.GetType().FullName} key - {key.GetHashCode()}
        /// or
        /// Unable to find entry - {key.GetType().FullName} key - {key.GetHashCode()}</exception>
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

        #endregion

        #region Private Methods

        /// <summary>
        /// Shift remove will shift all entries backwards until there is an empty entry
        /// </summary>
        /// <param name="index">The index.</param>
        [MethodImpl(256)]
        private void ShiftRemove(uint index)
        {
            //Get next entry
            var next = _info[index + 1];

            while (!next.IsEmpty() && next.Psl != 0)
            {
                //swap upper entry with lower
                Swap(ref _entries[index + 1], ref _entries[index]);

                //decrease psl by 1
                _info[index + 1].Psl--;

                //swap upper info with lower
                Swap(ref _info[index + 1], ref _info[index]);

                //increase index by one
                next = _info[++index];
            }
        }

        /// <summary>
        /// Emplaces a new entry without checking for key existence
        /// </summary>
        /// <param name="entry">The entry.</param>
        /// <param name="meta">The meta.</param>
        [MethodImpl(256)]
        private void EmplaceInternal(ref FastEntry<TKey, TValue> entry, ref InfoByte meta)
        {
            var hashcode = entry.Key.GetHashCode();
            uint index = (uint)hashcode * GoldenRatio >> _shift;

            var info = _info[index];
            if (info.IsEmpty())
            {
                _entries[index] = entry;
                _info[index] = meta;
                return;
            }

            ++index;

            meta.Psl = 0;

            for (; ; ++meta.Psl, ++index)
            {
                if (_currentProbeSequenceLength < meta.Psl)
                {
                    _currentProbeSequenceLength = meta.Psl;
                }

                info = _info[index];
                if (info.IsEmpty())
                {
                    _entries[index] = entry;
                    _info[index] = meta;
                    return;
                }

                if (meta.Psl > info.Psl)
                {
                    Swap(ref entry, ref _entries[index]);
                    Swap(ref meta, ref _info[index]);
                    continue;
                }

                if (meta.Psl == _maxProbeSequenceLength)
                {
                    IncreaseMaxProbeSequence();
                    Resize();
                    EmplaceInternal(ref entry, ref meta);
                    return;
                }
            }
        }

        private void IncreaseMaxProbeSequence()
        {
            if (_loadFactor <= 0.5d)
            {
                ++_maxProbeSequenceLength;
            }
            else
            {
                _maxProbeSequenceLength = _loadFactor <= 0.5
                    ? Log2(_maxlookups)
                    : PslLimit(_maxlookups);
            }
        }

        /// <summary>
        /// Swaps the content of the specified FastEntry values
        /// </summary>
        /// <param name="x">The x.</param>
        /// <param name="y">The y.</param>
        [MethodImpl(256)]
        private void Swap(ref FastEntry<TKey, TValue> x, ref FastEntry<TKey, TValue> y)
        {
            var tmp = x;
            x = y;
            y = tmp;
        }

        /// <summary>
        /// Swaps the content of the specified Infobyte values
        /// </summary>
        /// <param name="x">The x.</param>
        /// <param name="y">The y.</param>
        [MethodImpl(256)]
        private void Swap(ref InfoByte x, ref InfoByte y)
        {
            var tmp = x;
            x = y;
            y = tmp;
        }

        /// <summary>
        /// Returns a power of two probe sequence lengthzz
        /// </summary>
        /// <param name="size">The size.</param>
        /// <returns></returns>
        [MethodImpl(256)]
#pragma warning disable S1541 // Methods and properties should not be too complex
        private byte PslLimit(uint size)
        {
            switch (size)
            {
                case 16: return 4;
                case 32: return 5;
                case 64: return 6;
                case 128: return 7;
                case 256: return 8;
                case 512: return 9;
                case 1024: return 12;
                case 2048: return 15;
                case 4096: return 20;
                case 8192: return 25;
                case 16384: return 30;
                case 32768: return 35;
                case 65536: return 40;
                case 131072: return 45;
                case 262144: return 50;
                case 524288: return 55;
                case 1048576: return 60;
                case 2097152: return 65;
                case 4194304: return 70;
                case 8388608: return 75;
                case 16777216: return 80;
                case 33554432: return 85;
                case 67108864: return 90;
                case 134217728: return 95;
                case 268435456: return 100;
                case 536870912: return 105;
                default: return 10;
            }
        }

#pragma warning restore S1541 // Methods and properties should not be too complex
        /// <summary>
        /// Resizes this instance.
        /// </summary>
        [MethodImpl(256)]
        private void Resize()
        {
            _shift--;
            _maxlookups = NextPow2(_maxlookups + 1);

            var oldEntries = new FastEntry<TKey, TValue>[_entries.Length];
            Array.Copy(_entries, oldEntries, _entries.Length);

            var oldInfo = new InfoByte[_info.Length];
            Array.Copy(_info, oldInfo, _info.Length);

            _entries = new FastEntry<TKey, TValue>[_maxlookups + _maxProbeSequenceLength + 1];
            _info = new InfoByte[_maxlookups + _maxProbeSequenceLength + 1];
            
            for (var i = 0; i < oldEntries.Length; i++)
            {
                var info = oldInfo[i];
                if (info.IsEmpty())
                {
                    continue;
                }

                var entry = oldEntries[i];
                EmplaceInternal(ref entry, ref info);
            }
        }

        /// <summary>
        /// Calculates next power of 2
        /// </summary>
        /// <param name="c">The c.</param>
        /// <returns></returns>
        ///
        [MethodImpl(256)]
        private static uint NextPow2(uint c)
        {
            c--;
            c |= c >> 1;
            c |= c >> 2;
            c |= c >> 4;
            c |= c >> 8;
            c |= c >> 16;
            return ++c;
        }

        // Used for set checking operations (using enumerables) that rely on counting
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