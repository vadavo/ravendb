﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using Raven.Client.Exceptions;
using Raven.Server.Utils;
using Sparrow.Json;

namespace Raven.Server.Documents.Indexes.Static
{
    public class DynamicArray : DynamicObject, IOrderedEnumerable<object>
    {
        private readonly IEnumerable<object> _inner;

        public DynamicArray(IEnumerable inner)
            : this(inner.Cast<object>())
        {
        }

        public DynamicArray(IEnumerable<object> inner)
        {
            _inner = inner;
        }

        public dynamic Get(params int[] indexes)
        {
            if (indexes == null)
                return DynamicNullObject.Null;

            dynamic val = this;
            foreach (int index in indexes)
            {
                val = val[index];
            }
            return val;
        }

        public override bool TryGetMember(GetMemberBinder binder, out object result)
        {
            const string lengthName = "Length";
            const string countName = "Count";

            if (string.CompareOrdinal(binder.Name, lengthName) == 0 ||
                string.CompareOrdinal(binder.Name, countName) == 0)
            {
                result = _inner.Count();
                return true;
            }

            result = null;
            return false;
        }

        public override bool TryGetIndex(GetIndexBinder binder, object[] indexes, out object result)
        {
            if (indexes == null)
            {
                result = DynamicNullObject.Null;
                return true;
            }
            if (indexes.Length != 1)
            {
                var ints = new int[indexes.Length];
                for (int j = 0; j < indexes.Length; j++)
                {
                    if (indexes[j] is int num)
                        ints[j] = num;
                }
                result = Get(ints);
                return true;
            }

            if (!(indexes[0] is int i))
                i = Convert.ToInt32(indexes[0]);

            var resultObject = _inner.ElementAt(i);

            result = TypeConverter.ToDynamicType(resultObject);
            return true;
        }

        public override bool TryConvert(ConvertBinder binder, out object result)
        {
            if (binder.ReturnType.IsArray)
            {
                var elementType = binder.ReturnType.GetElementType();
                var count = _inner.Count();
                var array = Array.CreateInstance(elementType, count);

                for (var i = 0; i < count; i++)
                {
                    var item = _inner.ElementAt(i);
                    if (elementType == typeof(string) && (item is LazyStringValue || item is LazyCompressedStringValue))
                        array.SetValue(item.ToString(), i);
                    else
                        array.SetValue(Convert.ChangeType(item, elementType), i);
                }

                result = array;

                return true;
            }

            return base.TryConvert(binder, out result);
        }

        public dynamic this[int i]
        {
            get { return ElementAt(i); }
        }

        IEnumerator<object> IEnumerable<object>.GetEnumerator()
        {
            return GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public DynamicArrayIterator GetEnumerator()
        {
            return new DynamicArrayIterator(_inner);
        }

        public int Count() => _inner.Count();

        public int Count(Func<dynamic, bool> predicate) => Enumerable.Count(this, predicate);

        public dynamic Any()
        {
            return Enumerable.Any(this);
        }

        public dynamic Any(Func<dynamic, bool> predicate)
        {
            return Enumerable.Any(this, predicate);
        }

        public dynamic All(Func<dynamic, bool> predicate)
        {
            return Enumerable.All(this, predicate);
        }

        public int FindIndex(Predicate<dynamic> match)
        {
            var items = Enumerable.ToList(this);
            return items.FindIndex(match);
        }

        public int FindIndex(int startIndex, Predicate<dynamic> match)
        {
            var items = Enumerable.ToList(this);
            return items.FindIndex(startIndex, match);
        }

        public int FindIndex(int startIndex, int count, Predicate<dynamic> match)
        {
            var items = Enumerable.ToList(this);
            return items.FindIndex(startIndex, count, match);
        }

        public int FindLastIndex(Predicate<dynamic> match)
        {
            var items = Enumerable.ToList(this);
            return items.FindLastIndex(match);
        }

        public int FindLastIndex(int startIndex, Predicate<dynamic> match)
        {
            var items = Enumerable.ToList(this);
            return items.FindLastIndex(startIndex, match);
        }

        public int FindLastIndex(int startIndex, int count, Predicate<dynamic> match)
        {
            var items = Enumerable.ToList(this);
            return items.FindLastIndex(startIndex, count, match);
        }

        public dynamic First()
        {
            return Enumerable.First(this);
        }

        public dynamic First(Func<dynamic, bool> predicate)
        {
            return Enumerable.First(this, predicate);
        }

        public dynamic FirstOrDefault()
        {
            return Enumerable.FirstOrDefault(this) ?? DynamicNullObject.Null;
        }

        public dynamic FirstOrDefault(Func<dynamic, bool> predicate)
        {
            return Enumerable.FirstOrDefault(this, predicate) ?? DynamicNullObject.Null;
        }

        public dynamic Single()
        {
            return Enumerable.Single(this);
        }

        public dynamic Single(Func<dynamic, bool> predicate)
        {
            return Enumerable.Single(this, predicate);
        }

        public dynamic SingleOrDefault()
        {
            return Enumerable.SingleOrDefault(this) ?? DynamicNullObject.Null;
        }

        public dynamic SingleOrDefault(Func<dynamic, bool> predicate)
        {
            return Enumerable.SingleOrDefault(this, predicate) ?? DynamicNullObject.Null;
        }

        public bool Contains(object item)
        {
            var itemToWorkOn = InternalConvert(item);

            return Enumerable.Contains(this, itemToWorkOn);
        }

        public int Sum(Func<dynamic, int> selector)
        {
            return Enumerable.Sum(this, selector);
        }

        public int? Sum(Func<dynamic, int?> selector)
        {
            return Enumerable.Sum(this, selector) ?? DynamicNullObject.Null;
        }

        public long Sum(Func<dynamic, long> selector)
        {
            return Enumerable.Sum(this, selector);
        }

        public long? Sum(Func<dynamic, long?> selector)
        {
            return Enumerable.Sum(this, selector) ?? DynamicNullObject.Null;
        }

        public float Sum(Func<dynamic, float> selector)
        {
            return Enumerable.Sum(this, selector);
        }

        public float? Sum(Func<dynamic, float?> selector)
        {
            return Enumerable.Sum(this, selector) ?? DynamicNullObject.Null;
        }

        public double Sum(Func<dynamic, double> selector)
        {
            return Enumerable.Sum(this, selector);
        }

        public double? Sum(Func<dynamic, double?> selector)
        {
            return Enumerable.Sum(this, selector) ?? DynamicNullObject.Null;
        }

        public decimal Sum(Func<dynamic, decimal> selector)
        {
            return Enumerable.Sum(this, selector);
        }

        public decimal? Sum(Func<dynamic, decimal?> selector)
        {
            return Enumerable.Sum(this, selector) ?? DynamicNullObject.Null;
        }

        public dynamic Min()
        {
            return Enumerable.Min(this) ?? DynamicNullObject.Null;
        }

        public dynamic Min<TResult>(Func<dynamic, TResult> selector)
        {
            var result = Enumerable.Min(this, selector);

            if (result == null)
                return DynamicNullObject.Null;

            return result;
        }

        public dynamic Max()
        {
            return Enumerable.Max(this) ?? DynamicNullObject.Null;
        }

        public dynamic Max<TResult>(Func<dynamic, TResult> selector)
        {
            var result = Enumerable.Max(this, selector);

            if (result == null)
                return DynamicNullObject.Null;

            return result;
        }

        public double Average(Func<dynamic, int> selector)
        {
            return Enumerable.Average(this, selector);
        }

        public double? Average(Func<dynamic, int?> selector)
        {
            return Enumerable.Average(this, selector) ?? DynamicNullObject.Null;
        }

        public double Average(Func<dynamic, long> selector)
        {
            return Enumerable.Average(this, selector);
        }

        public double? Average(Func<dynamic, long?> selector)
        {
            return Enumerable.Average(this, selector) ?? DynamicNullObject.Null;
        }

        public float Average(Func<dynamic, float> selector)
        {
            return Enumerable.Average(this, selector);
        }

        public float? Average(Func<dynamic, float?> selector)
        {
            return Enumerable.Average(this, selector) ?? DynamicNullObject.Null;
        }

        public double Average(Func<dynamic, double> selector)
        {
            return Enumerable.Average(this, selector);
        }

        public double? Average(Func<dynamic, double?> selector)
        {
            return Enumerable.Average(this, selector) ?? DynamicNullObject.Null;
        }

        public decimal Average(Func<dynamic, decimal> selector)
        {
            return Enumerable.Average(this, selector);
        }

        public decimal? Average(Func<dynamic, decimal?> selector)
        {
            return Enumerable.Average(this, selector) ?? DynamicNullObject.Null;
        }

        public IEnumerable<dynamic> OrderBy(Func<dynamic, dynamic> comparable)
        {
            return new DynamicArray(Enumerable.OrderBy(this, comparable));
        }

        public IEnumerable<dynamic> OrderBy(Func<IGrouping<dynamic, dynamic>, dynamic> comparable)
        {
            return new DynamicArray(_inner.Cast<DynamicGrouping>().OrderBy(comparable));
        }

        public IEnumerable<dynamic> OrderByDescending(Func<dynamic, dynamic> comparable)
        {
            return new DynamicArray(Enumerable.OrderByDescending(this, comparable));
        }

        public IEnumerable<dynamic> OrderByDescending(Func<IGrouping<dynamic, dynamic>, dynamic> comparable)
        {
            return new DynamicArray(_inner.Cast<DynamicGrouping>().OrderByDescending(comparable));
        }

        private IOrderedEnumerable<object> CreateOrderedEnumerable<TKey>(Func<object, TKey> keySelector, IComparer<TKey> comparer, bool descending, int depth)
        {
            if (_inner is not DynamicArray && _inner is IOrderedEnumerable<object> orderedEnumerable)
            {
                return descending
                    ? new DynamicArray(Enumerable.ThenByDescending(orderedEnumerable, keySelector, comparer))
                    : new DynamicArray(Enumerable.ThenBy(orderedEnumerable, keySelector, comparer));
            }

            if (_inner is not DynamicArray dynamicArray)
            {
                return descending
                    ? new DynamicArray(Enumerable.OrderByDescending(_inner, keySelector, comparer))
                    : new DynamicArray(Enumerable.OrderBy(_inner, keySelector, comparer));
            }

            if (depth == 0)
            {
                throw new InvalidQueryException($"Cannot create {nameof(IOrderedEnumerable<object>)} because your query is too complex. Please rewrite your ordering query.");
            }

            return dynamicArray.CreateOrderedEnumerable(keySelector, comparer, descending, depth - 1);
        }


        public IOrderedEnumerable<object> CreateOrderedEnumerable<TKey>(Func<object, TKey> keySelector, IComparer<TKey> comparer, bool descending)
        {
            return CreateOrderedEnumerable(keySelector, comparer, descending, 32);
        }

        public IEnumerable<dynamic> ThenBy(Func<dynamic, dynamic> comparable)
        {
            return new DynamicArray(Enumerable.ThenBy(this, comparable));
        }

        public IEnumerable<dynamic> ThenBy(Func<dynamic, dynamic> comparable, IComparer<dynamic> comparer)
        {
            return new DynamicArray(Enumerable.ThenBy(this, comparable, comparer));
        }

        public IEnumerable<dynamic> ThenByDescending(Func<dynamic, dynamic> keySelector)
        {
            return new DynamicArray(Enumerable.ThenByDescending(this, keySelector));
        }

        public IEnumerable<dynamic> ThenByDescending(Func<dynamic, dynamic> keySelector, IComparer<dynamic> comparer)
        {
            return new DynamicArray(Enumerable.ThenByDescending(this, keySelector, comparer));
        }

        public dynamic GroupBy(Func<dynamic, dynamic> keySelector)
        {
            return new DynamicArray(Enumerable.GroupBy(this, keySelector).Select(x => new DynamicGrouping(x)));
        }

        public dynamic GroupBy(Func<dynamic, dynamic> keySelector, Func<dynamic, dynamic> selector)
        {
            return new DynamicArray(Enumerable.GroupBy(this, keySelector, selector).Select(x => new DynamicGrouping(x)));
        }

        public dynamic Last()
        {
            return Enumerable.Last(this);
        }

        public dynamic LastOrDefault()
        {
            return Enumerable.LastOrDefault(this) ?? DynamicNullObject.Null;
        }

        public dynamic Last(Func<dynamic, bool> predicate)
        {
            return Enumerable.Last(this, predicate);
        }

        public dynamic LastOrDefault(Func<dynamic, bool> predicate)
        {
            return Enumerable.LastOrDefault(this, predicate) ?? DynamicNullObject.Null;
        }

        public dynamic IndexOf(dynamic item)
        {
            return IndexOf(item, -1, -1);
        }

        public dynamic IndexOf(dynamic item, int index)
        {
            return IndexOf(item, index, -1);
        }

        public dynamic IndexOf(dynamic item, int index, int count)
        {
            var items = Enumerable.ToList(this);

            if (index > -1 && count > -1)
                return IndexOfInternal(item, items, index, count);

            if (index > -1)
                return IndexOfInternal(item, items, index, -1);

            return IndexOfInternal(item, items, 0, items.Count);
        }

        private static dynamic IndexOfInternal(dynamic item, List<object> items, int index, int count)
        {
            var itemToWorkOn = InternalConvert(item);

            if (count == -1)
                return items.IndexOf(itemToWorkOn, index);

            return items.IndexOf(itemToWorkOn, index, count);
        }

        public dynamic LastIndexOf(dynamic item)
        {
            return LastIndexOf(item, -1, -1);
        }

        public dynamic LastIndexOf(dynamic item, int index)
        {
            return LastIndexOf(item, index, -1);
        }

        public dynamic LastIndexOf(dynamic item, int index, int count)
        {
            var items = Enumerable.ToList(this);

            if (index > -1 && count > -1)
                return LastIndexOfInternal(item, items, index, count);

            return index > -1 ? LastIndexOfInternal(item, items, index, -1) : LastIndexOfInternal(item, items, items.Count - 1, items.Count);
        }

        private static dynamic LastIndexOfInternal(dynamic item, List<object> items, int index, int count)
        {
            var itemToWorkOn = InternalConvert(item);

            return count == -1 ? items.LastIndexOf(itemToWorkOn, index) : items.LastIndexOf(itemToWorkOn, index, count);
        }

        private static dynamic InternalConvert(dynamic item)
        {
            switch (item)
            {
                case int _:
                case short _:
                    return Convert.ToInt64(item);
                case float _:
                    return Convert.ToDouble(item);
                case char _:
                    return Convert.ToString(item);
                default:
                    return item;
            }
        }

        public IEnumerable<dynamic> Take(int count)
        {
            return new DynamicArray(Enumerable.Take(this, count));
        }

        public IEnumerable<dynamic> Skip(int count)
        {
            return new DynamicArray(Enumerable.Skip(this, count));
        }

        public IEnumerable<object> Select(Func<object, object> func)
        {
            return new DynamicArray(Enumerable.Select(this, func));
        }

        public IEnumerable<object> Select(Func<IGrouping<object, object>, object> func)
        {
            return new DynamicArray(Enumerable.Select(this, o => func((IGrouping<object, object>)o)));
        }

        public IEnumerable<object> Select(Func<object, int, object> func)
        {
            return new DynamicArray(Enumerable.Select(this, func));
        }

        public IEnumerable<object> SelectMany(Func<object, IEnumerable<object>> func)
        {
            return new DynamicArray(Enumerable.SelectMany(this, func));
        }

        public IEnumerable<object> SelectMany(Func<object, IEnumerable<object>> func, Func<object, object, object> selector)
        {
            return new DynamicArray(Enumerable.SelectMany(this, func, selector));
        }

        public IEnumerable<object> SelectMany(Func<object, int, IEnumerable<object>> func)
        {
            return new DynamicArray(Enumerable.SelectMany(this, func));
        }

        public IEnumerable<object> Where(Func<object, bool> func)
        {
            return new DynamicArray(Enumerable.Where(this, func));
        }

        public IEnumerable<object> Where(Func<object, int, bool> func)
        {
            return new DynamicArray(Enumerable.Where(this, func));
        }

        public IEnumerable<object> Distinct()
        {
            return new DynamicArray(Enumerable.Distinct(this, new LazyStringAwareEqualityComparerForDistinct(CurrentIndexingScope.Current?.IndexContext)));
        }

        public dynamic DefaultIfEmpty(object defaultValue = null)
        {
            return Enumerable.DefaultIfEmpty(this, defaultValue ?? DynamicNullObject.Null);
        }

        public IEnumerable<dynamic> Except(IEnumerable<dynamic> except)
        {
            return new DynamicArray(Enumerable.Except(this, except));
        }

        public IEnumerable<dynamic> Reverse()
        {
            return new DynamicArray(Enumerable.Reverse(this));
        }

        public bool SequenceEqual(IEnumerable<dynamic> second)
        {
            return Enumerable.SequenceEqual(this, second);
        }

        public IEnumerable<dynamic> AsEnumerable()
        {
            return this;
        }

        public dynamic[] ToArray()
        {
            return Enumerable.ToArray(this);
        }

        public List<dynamic> ToList()
        {
            return Enumerable.ToList(this);
        }

        public IDictionary<dynamic, dynamic> ToDictionary(Func<IGrouping<dynamic, dynamic>, dynamic> keySelector)
        {
            return new DynamicDictionary(Enumerable.ToDictionary(this, o => keySelector((IGrouping<object, object>)o)));
        }

        public IDictionary<dynamic, dynamic> ToDictionary(Func<IGrouping<dynamic, dynamic>, dynamic> keySelector, IEqualityComparer<dynamic> comparer)
        {
            return new DynamicDictionary(Enumerable.ToDictionary(this, o => keySelector((IGrouping<object, object>)o), comparer));
        }

        public IDictionary<dynamic, dynamic> ToDictionary(Func<IGrouping<dynamic, dynamic>, dynamic> keySelector, Func<IGrouping<dynamic, dynamic>, dynamic> elementSelector)
        {
            return new DynamicDictionary(Enumerable.ToDictionary(this, o => keySelector((IGrouping<object, object>)o), u => elementSelector((IGrouping<object, object>)u)));
        }

        public IDictionary<dynamic, dynamic> ToDictionary(Func<IGrouping<dynamic, dynamic>, dynamic> keySelector, Func<IGrouping<dynamic, dynamic>, dynamic> elementSelector, IEqualityComparer<dynamic> comparer)
        {
            return new DynamicDictionary(Enumerable.ToDictionary(this, o => keySelector((IGrouping<object, object>)o), u => elementSelector((IGrouping<object, object>)u), comparer));
        }

        public IDictionary<dynamic, dynamic> ToDictionary(Func<dynamic, dynamic> keySelector)
        {
            return new DynamicDictionary(Enumerable.ToDictionary(this, keySelector));
        }

        public IDictionary<dynamic, dynamic> ToDictionary(Func<dynamic, dynamic> keySelector, IEqualityComparer<dynamic> comparer)
        {
            return new DynamicDictionary(Enumerable.ToDictionary(this, keySelector, comparer));
        }

        public IDictionary<dynamic, dynamic> ToDictionary(Func<dynamic, dynamic> keySelector, Func<dynamic, dynamic> elementSelector)
        {
            return new DynamicDictionary(Enumerable.ToDictionary(this, keySelector, elementSelector));
        }

        public IDictionary<dynamic, dynamic> ToDictionary(Func<dynamic, dynamic> keySelector, Func<dynamic, dynamic> elementSelector, IEqualityComparer<dynamic> comparer)
        {
            return new DynamicDictionary(Enumerable.ToDictionary(this, keySelector, elementSelector, comparer));
        }

        public ILookup<TKey, dynamic> ToLookup<TKey>(Func<dynamic, TKey> keySelector, Func<dynamic, dynamic> elementSelector = null)
        {
            if (elementSelector == null)
                return Enumerable.ToLookup(this, keySelector);

            return Enumerable.ToLookup(this, keySelector, elementSelector);
        }

        public IEnumerable<dynamic> OfType<T>()
        {
            return new DynamicArray(Enumerable.OfType<T>(this));
        }

        public IEnumerable<dynamic> Cast<T>()
        {
            return new DynamicArray(Enumerable.Cast<T>(this));
        }

        public dynamic ElementAt(int index)
        {
            return Enumerable.ElementAt(this, index);
        }

        public dynamic ElementAtOrDefault(int index)
        {
            return Enumerable.ElementAtOrDefault(this, index) ?? DynamicNullObject.Null;
        }

        public long LongCount()
        {
            return Enumerable.LongCount(this);
        }

        public long LongCount(Func<dynamic, bool> predicate)
        {
            return Enumerable.LongCount(this, predicate);
        }

        public dynamic Aggregate(Func<dynamic, dynamic, dynamic> func)
        {
            return Enumerable.Aggregate(this, func);
        }

        public dynamic Aggregate(dynamic seed, Func<dynamic, dynamic, dynamic> func)
        {
            return Enumerable.Aggregate(this, (object)seed, func);
        }

        public dynamic Aggregate(dynamic seed, Func<dynamic, dynamic, dynamic> func, Func<dynamic, dynamic> resultSelector)
        {
            return Enumerable.Aggregate(this, (object)seed, func, resultSelector);
        }

        public IEnumerable<dynamic> TakeWhile(Func<dynamic, bool> predicate)
        {
            return new DynamicArray(Enumerable.TakeWhile(this, predicate));
        }

        public IEnumerable<dynamic> TakeWhile(Func<dynamic, int, bool> predicate)
        {
            return new DynamicArray(Enumerable.TakeWhile(this, predicate));
        }

        public IEnumerable<dynamic> SkipWhile(Func<dynamic, bool> predicate)
        {
            return new DynamicArray(Enumerable.SkipWhile(this, predicate));
        }

        public IEnumerable<dynamic> SkipWhile(Func<dynamic, int, bool> predicate)
        {
            return new DynamicArray(Enumerable.SkipWhile(this, predicate));
        }

        public IEnumerable<dynamic> Join(IEnumerable<dynamic> items, Func<dynamic, dynamic> outerKeySelector, Func<dynamic, dynamic> innerKeySelector,
                                            Func<dynamic, dynamic, dynamic> resultSelector)
        {
            return new DynamicArray(Enumerable.Join(this, items, outerKeySelector, innerKeySelector, resultSelector));
        }

        public IEnumerable<dynamic> GroupJoin(IEnumerable<dynamic> items, Func<dynamic, dynamic> outerKeySelector, Func<dynamic, dynamic> innerKeySelector,
                                            Func<dynamic, dynamic, dynamic> resultSelector)
        {
            return new DynamicArray(Enumerable.GroupJoin(this, items, outerKeySelector, innerKeySelector, resultSelector));
        }

        public IEnumerable<dynamic> Concat(IEnumerable second)
        {
            return new DynamicArray(Enumerable.Concat(this, second.Cast<object>()));
        }

        public IEnumerable<dynamic> Zip(IEnumerable second, Func<dynamic, dynamic, dynamic> resultSelector)
        {
            return new DynamicArray(Enumerable.Zip(this, second.Cast<object>(), resultSelector));
        }

        public IEnumerable<dynamic> Union(IEnumerable second)
        {
            return new DynamicArray(Enumerable.Union(this, second.Cast<object>()));
        }

        public IEnumerable<dynamic> Intersect(IEnumerable second)
        {
            return new DynamicArray(Enumerable.Intersect(this, second.Cast<object>()));
        }

        public struct DynamicArrayIterator : IEnumerator<object>
        {
            private readonly IEnumerator<object> _inner;

            public DynamicArrayIterator(IEnumerable<object> items)
            {
                _inner = items.GetEnumerator();
                Current = null;
            }

            public bool MoveNext()
            {
                if (_inner.MoveNext() == false)
                    return false;


                Current = TypeConverter.ToDynamicType(_inner.Current);
                return true;
            }

            public void Reset()
            {
                throw new NotImplementedException();
            }

            public object Current { get; private set; }

            object IEnumerator.Current => Current;

            public void Dispose()
            {
            }
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj))
                return false;

            if (ReferenceEquals(this, obj))
                return true;

            var array = obj as DynamicArray;

            if (array != null)
                return Equals(_inner, array._inner);

            return Equals(_inner, obj);
        }

        public override int GetHashCode()
        {
            return _inner?.GetHashCode() ?? 0;
        }

        public class DynamicGrouping : DynamicArray, IGrouping<object, object>
        {
            private readonly IGrouping<dynamic, dynamic> _grouping;

            public DynamicGrouping(IGrouping<dynamic, dynamic> grouping)
                : base(grouping)
            {
                _grouping = grouping;
            }

            public dynamic Key => _grouping.Key;

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }

        private class LazyStringAwareEqualityComparerForDistinct : IEqualityComparer<object>
        {
            private readonly JsonOperationContext _context;

            public LazyStringAwareEqualityComparerForDistinct(JsonOperationContext context)
            {
                _context = context;
            }

            public new bool Equals(object x, object y)
            {
                if (_context == null)
                    return EqualityComparer<object>.Default.Equals(x, y);

                if (x is string xAsString && y is string yAsString)
                    return xAsString.Equals(yAsString);

                if (x is LazyStringValue xLsv && y is string yAsString2)
                {
                    using (var yInner = _context.GetLazyString(yAsString2))
                        return xLsv.Equals(yInner);
                }

                if (x is LazyCompressedStringValue xLcsv && y is string yAsString3)
                {
                    using (var xInner = xLcsv.ToLazyStringValue())
                    using (var yInner = _context.GetLazyString(yAsString3))
                        return xInner.Equals(yInner);
                }

                if (x is string xAsString2 && y is LazyStringValue yLsv)
                {
                    using (var xInner = _context.GetLazyString(xAsString2))
                        return xInner.Equals(yLsv);
                }

                if (x is string xAsString3 && y is LazyCompressedStringValue yLcsv)
                {
                    using (var yInner = yLcsv.ToLazyStringValue())
                    using (var xInner = _context.GetLazyString(xAsString3))
                        return xInner.Equals(yInner);
                }

                return EqualityComparer<object>.Default.Equals(x, y);
            }

            public int GetHashCode(object obj)
            {
                if (_context == null || obj is not string s)
                    return EqualityComparer<object>.Default.GetHashCode(obj);

                using (var lsv = _context.GetLazyString(s))
                    return lsv.GetHashCode();
            }
        }

    }
}
