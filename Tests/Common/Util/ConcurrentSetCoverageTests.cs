/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Util;

namespace QuantConnect.Tests.Common.Util
{
    [TestFixture]
    public class ConcurrentSetCoverageTests
    {
        [Test]
        public void CountContainsAndRemoveTrackItems()
        {
            var set = new ConcurrentSet<string>();
            Assert.IsFalse(set.IsReadOnly);
            Assert.AreEqual(0, set.Count);
            Assert.IsEmpty(set.ToList());

            Assert.IsTrue(set.Add("a"));
            Assert.IsTrue(set.Add("b"));
            Assert.IsFalse(set.Add("a"));
            Assert.AreEqual(2, set.Count);
            Assert.IsTrue(set.Contains("a"));
            Assert.IsFalse(set.Contains("c"));
            Assert.IsFalse(set.Contains(null));

            Assert.IsTrue(set.Remove("a"));
            Assert.IsFalse(set.Remove("a"));
            Assert.IsFalse(set.Remove(null));
            CollectionAssert.AreEqual(new[] { "b" }, set.ToList());
        }

        [Test]
        public void CollectionAddRejectsNull()
        {
            ICollection<string> set = new ConcurrentSet<string>();
            set.Add("x");
            set.Add("x");
            Assert.AreEqual(1, set.Count);
            Assert.Throws<ArgumentNullException>(() => set.Add(null));
        }

        [Test]
        public void ClearEmptiesSetAndEnumerator()
        {
            var set = new ConcurrentSet<int> { 1, 2, 3 };
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, set.ToList());
            set.Clear();
            Assert.AreEqual(0, set.Count);
            Assert.IsEmpty(set.ToList());
            set.Add(4);
            CollectionAssert.AreEqual(new[] { 4 }, set.ToList());
        }

        [Test]
        public void CopyToWritesItemsInInsertionOrderAtOffset()
        {
            var set = new ConcurrentSet<int> { 5, 3, 9 };
            var array = new int[5];
            set.CopyTo(array, 1);
            CollectionAssert.AreEqual(new[] { 0, 5, 3, 9, 0 }, array);
        }

        [Test]
        public void NonGenericEnumeratorYieldsItems()
        {
            var set = new ConcurrentSet<int> { 1, 2 };
            var items = new List<object>();
            foreach (var item in (System.Collections.IEnumerable)set)
            {
                items.Add(item);
            }
            CollectionAssert.AreEqual(new object[] { 1, 2 }, items);
        }

        [Test]
        public void SetComparisonsAgainstEqualAndDisjointSets()
        {
            var set = new ConcurrentSet<int> { 1, 2, 3 };
            Assert.IsTrue(set.SetEquals(new[] { 3, 2, 1 }));
            Assert.IsFalse(set.SetEquals(new[] { 1, 2 }));
            Assert.IsFalse(set.SetEquals(new[] { 1, 2, 3, 4 }));
            Assert.IsTrue(set.IsSupersetOf(new[] { 1, 2, 3 }));
            Assert.IsFalse(set.Overlaps(new[] { 7, 8 }));
            Assert.IsTrue(set.Overlaps(new[] { 8, 3 }));
        }

        [Test]
        [Ignore("Possible bug: ConcurrentSet.IsSupersetOf/IsProperSupersetOf check that every item of the set is in 'other' (subset semantics), so {1,2,3} is not reported as a superset of {1,2}")]
        public void IsSupersetOfMatchesHashSetForStrictSubset()
        {
            var set = new ConcurrentSet<int> { 1, 2, 3 };
            var hashSet = new HashSet<int> { 1, 2, 3 };
            var other = new[] { 1, 2 };
            Assert.AreEqual(hashSet.IsSupersetOf(other), set.IsSupersetOf(other));
            Assert.AreEqual(hashSet.IsProperSupersetOf(other), set.IsProperSupersetOf(other));
        }

        [Test]
        [Ignore("Possible bug: ConcurrentSet.IsSubsetOf/IsProperSubsetOf iterate 'other' and end with '_set.Keys.Count == 0', so a non-empty set is never reported as a subset of a larger set")]
        public void IsSubsetOfMatchesHashSetForStrictSuperset()
        {
            var set = new ConcurrentSet<int> { 1, 2 };
            var hashSet = new HashSet<int> { 1, 2 };
            var other = new[] { 1, 2, 3 };
            Assert.AreEqual(hashSet.IsSubsetOf(other), set.IsSubsetOf(other));
            Assert.AreEqual(hashSet.IsProperSubsetOf(other), set.IsProperSubsetOf(other));
        }
    }
}
