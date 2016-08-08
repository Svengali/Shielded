﻿using System;
using NUnit.Framework;
using Shielded;
using System.Threading;
using System.Linq;
using System.Collections.Generic;

namespace ShieldedTests
{
    [TestFixture]
    public class SyncSideEffectTests
    {
        [Test]
        public void BasicSyncSideEffect()
        {
            var a = new Shielded<int>();
            Shield.InTransaction(() => {
                a.Value = 10;
                Shield.SyncSideEffect(() => {
                    var t = new Thread(() => {
                        Assert.IsFalse(Shield.IsInTransaction);
                        // attempting to do this in a transaction would cause a deadlock!
                        Assert.AreEqual(0, a);
                    });
                    t.Start();
                    t.Join();
                    Assert.AreEqual(10, a);
                });
            });
        }

        private bool IsSorted(int[] coll)
        {
            for (int i = 0; i < coll.Length - 1; i++)
                if (coll[i] > coll[i + 1])
                    return false;
            return true;
        }

        [Test]
        public void OrderedSideEffects()
        {
            int numRuns = 10000;
            var x = new Shielded<int>();

            var normalFx = new int[numRuns];
            int place = -1;
            ParallelEnumerable.Repeat(0, numRuns).ForAll(_ => {
                Shield.InTransaction(() => {
                    int old = x;
                    x.Value = old + 1;
                    Shield.SideEffect(() => {
                        int taken = Interlocked.Increment(ref place);
                        normalFx[taken] = old;
                    });
                });
            });

            var syncFx = new int[numRuns];
            place = -1;
            ParallelEnumerable.Repeat(0, numRuns).ForAll(_ => {
                Shield.InTransaction(() => {
                    int old = x;
                    x.Value = old + 1;
                    Shield.SyncSideEffect(() => {
                        int taken = Interlocked.Increment(ref place);
                        syncFx[taken] = old;
                    });
                });
            });

            Assert.IsTrue(IsSorted(syncFx));
            if (IsSorted(normalFx))
                Assert.Inconclusive();
        }

        [Test]
        public void SyncSideEffectInReadOnlyTrans()
        {
            var x = new Shielded<int>(10);
            bool didItRun = false;
            Shield.InTransaction(() => {
                int i = x;
                // it will run even in a read-only transaction, even though such transactions
                // do not lock anything, so it's really just like an ordinary side-effect.
                Shield.SyncSideEffect(() => didItRun = true);
            });
            Assert.IsTrue(didItRun);
        }
    }
}

