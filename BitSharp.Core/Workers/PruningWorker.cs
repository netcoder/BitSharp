﻿using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Core.Builders;
using BitSharp.Core.Domain;
using BitSharp.Core.Storage;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;

namespace BitSharp.Core.Workers
{
    internal class PruningWorker : Worker
    {
        private readonly Logger logger = LogManager.GetCurrentClassLogger();

        private readonly ICoreDaemon coreDaemon;
        private readonly IStorageManager storageManager;
        private readonly ChainStateWorker chainStateWorker;

        private readonly WorkerPool gatherWorkers;
        private readonly WorkerPool pruneBlockWorkers;

        private readonly AverageMeasure txCountMeasure = new AverageMeasure();
        private readonly AverageMeasure txRateMeasure = new AverageMeasure();
        private readonly DurationMeasure totalDurationMeasure = new DurationMeasure();
        private readonly DurationMeasure gatherIndexDurationMeasure = new DurationMeasure();
        private readonly DurationMeasure pruneIndexDurationMeasure = new DurationMeasure();
        private readonly DurationMeasure pruneBlocksDurationMeasure = new DurationMeasure();
        private readonly DurationMeasure commitDurationMeasure = new DurationMeasure();

        //TODO
        private ChainBuilder prunedChain;

        public PruningWorker(WorkerConfig workerConfig, ICoreDaemon coreDaemon, IStorageManager storageManager, ChainStateWorker chainStateWorker)
            : base("PruningWorker", workerConfig.initialNotify, workerConfig.minIdleTime, workerConfig.maxIdleTime)
        {
            this.coreDaemon = coreDaemon;
            this.storageManager = storageManager;
            this.chainStateWorker = chainStateWorker;

            this.prunedChain = new ChainBuilder();
            this.Mode = PruningMode.None;

            var gatherThreadCount = Environment.ProcessorCount * 2;
            this.gatherWorkers = new WorkerPool("PruningWorker.GatherWorkers", gatherThreadCount);

            // initialize a pool of pruning workers
            //TODO
            var txesPruneThreadCount = storageManager.GetType().Name.Contains("Lmdb") ? 1 : Environment.ProcessorCount * 2;
            this.pruneBlockWorkers = new WorkerPool("PruningWorker.PruneBlockWorkers", txesPruneThreadCount);
        }

        protected override void SubDispose()
        {
            this.gatherWorkers.Dispose();
            this.pruneBlockWorkers.Dispose();
            this.txCountMeasure.Dispose();
            this.txRateMeasure.Dispose();
            this.totalDurationMeasure.Dispose();
            this.gatherIndexDurationMeasure.Dispose();
            this.pruneIndexDurationMeasure.Dispose();
            this.pruneBlocksDurationMeasure.Dispose();
            this.commitDurationMeasure.Dispose();
        }

        public PruningMode Mode { get; set; }

        public int PrunableHeight { get; set; }

        private DateTime lastLogTime = DateTime.Now;
        protected override void WorkAction()
        {
            // check if pruning is turned off
            var mode = this.Mode;
            if (mode == PruningMode.None)
                return;

            var startHeight = this.prunedChain.Height;

            // navigate from the current pruned chain towards the current processed chain
            foreach (var pathElement in this.prunedChain.ToImmutable().NavigateTowards(() => this.coreDaemon.CurrentChain))
            {
                // cooperative loop
                if (!this.IsStarted)
                    break;

                // get candidate block to be pruned
                var direction = pathElement.Item1;
                var chainedHeader = pathElement.Item2;

                // get the current processed chain
                var processedChain = this.coreDaemon.CurrentChain;

                // determine maximum safe pruning height, based on a buffer distance from the processed chain height
                var blocksPerDay = 144;
                var pruneBuffer = blocksPerDay * 7;
                var maxHeight = processedChain.Height - pruneBuffer;
                //TODO
                maxHeight = Math.Min(maxHeight, this.PrunableHeight);

                // check if this block is safe to prune
                if (chainedHeader.Height > maxHeight)
                    break;

                if (direction > 0)
                {
                    // prune the block
                    this.PruneBlock(mode, processedChain, chainedHeader);

                    // track pruned block
                    this.prunedChain.AddBlock(chainedHeader);
                }
                else if (direction < 0)
                {
                    // pruning should not roll back, the buffer is in place to prevent this on orphans
                    throw new InvalidOperationException();
                }
                else
                {
                    Debugger.Break();
                    throw new InvalidOperationException();
                }

                // pause chain state processing when pruning lags too far behind
                var isLagging = maxHeight - chainedHeader.Height > 100;
                if (isLagging)
                {
                    //TODO better way to block chain state worker when pruning is behind
                    if (this.chainStateWorker != null && this.chainStateWorker.IsStarted)
                    {
                        this.logger.Info("Pausing chain state processing, pruning is lagging.");
                        this.chainStateWorker.Stop(TimeSpan.Zero);
                    }
                }
                else
                {
                    //TODO better way to block chain state worker when pruning is behind
                    if (this.chainStateWorker != null && !this.chainStateWorker.IsStarted)
                    {
                        this.logger.Info("Resuming chain state processing, pruning is caught up.");
                        this.chainStateWorker.Start();
                        this.chainStateWorker.NotifyWork();
                    }
                }

                // log pruning stats periodically
                if (DateTime.Now - lastLogTime > TimeSpan.FromSeconds(15))
                {
                    lastLogTime = DateTime.Now;
                    var stopHeight = this.prunedChain.Height;

                    this.logger.Info(
    @"Pruned from block {0:#,##0} to {1:#,##0}:
- avg tx rate:    {2,8:#,##0}/s
- per block stats:
- tx count:       {3,8:#,##0}
- gather index:   {4,12:#,##0.000}s
- prune blocks:   {5,12:#,##0.000}s
- prune index:    {6,12:#,##0.000}s
- commit:         {7,12:#,##0.000}s
- TOTAL:          {8,12:#,##0.000}s"
                        .Format2(startHeight, stopHeight, txRateMeasure.GetAverage(), txCountMeasure.GetAverage(), gatherIndexDurationMeasure.GetAverage().TotalSeconds, pruneBlocksDurationMeasure.GetAverage().TotalSeconds, pruneIndexDurationMeasure.GetAverage().TotalSeconds, commitDurationMeasure.GetAverage().TotalSeconds, totalDurationMeasure.GetAverage().TotalSeconds));

                    startHeight = stopHeight + 1;
                }
            }

            // ensure chain state processing is resumed
            if (this.chainStateWorker != null && !this.chainStateWorker.IsStarted)
            {
                this.logger.Info("Resuming chain state processing, pruning is caught up.");
                this.chainStateWorker.Start();
                this.chainStateWorker.NotifyWork();
            }
        }

        private void PruneBlock(PruningMode mode, Chain chain, ChainedHeader pruneBlock)
        {
            //TODO the replay information about blocks that have been rolled back also needs to be pruned (UnmintedTx)

            var txCount = 0;
            var totalStopwatch = Stopwatch.StartNew();
            var gatherIndexStopwatch = new Stopwatch();
            var pruneIndexStopwatch = new Stopwatch();
            var pruneBlocksStopwatch = new Stopwatch();
            var commitStopwatch = new Stopwatch();

            // retrieve the spent txes for this block
            IImmutableList<UInt256> spentTxes;
            using (var handle = this.storageManager.OpenChainStateCursor())
            {
                var chainStateCursor = handle.Item;

                chainStateCursor.BeginTransaction(readOnly: true);
                try
                {
                    chainStateCursor.TryGetBlockSpentTxes(pruneBlock.Height, out spentTxes);
                }
                finally
                {
                    chainStateCursor.RollbackTransaction();
                }
            }

            if (spentTxes != null)
            {
                txCount = spentTxes.Count;

                if (mode.HasFlag(PruningMode.BlockTxesPreserveMerkle) || mode.HasFlag(PruningMode.BlockTxesDestroyMerkle))
                {
                    // dictionary to keep track of spent transactions against their block
                    var pruneData = new ConcurrentDictionary<int, ConcurrentBag<int>>();

                    gatherIndexStopwatch.Time(() =>
                    {
                        using (var spentTxesQueue = new ConcurrentBlockingQueue<UInt256>())
                        {
                            spentTxesQueue.AddRange(spentTxes);
                            spentTxesQueue.CompleteAdding();

                            this.gatherWorkers.Do(() =>
                            {
                                var emptyBag = new ConcurrentBag<int>();
                                using (var handle = this.storageManager.OpenChainStateCursor())
                                {
                                    var chainStateCursor = handle.Item;

                                    chainStateCursor.BeginTransaction(readOnly: true);
                                    try
                                    {
                                        foreach (var spentTxHash in spentTxesQueue.GetConsumingEnumerable())
                                        {
                                            UnspentTx spentTx;
                                            if (chainStateCursor.TryGetUnspentTx(spentTxHash, out spentTx))
                                            {
                                                // queue up spent tx to be pruned from block txes
                                                ConcurrentBag<int> blockTxIndices;
                                                if (pruneData.TryAdd(spentTx.BlockIndex, emptyBag))
                                                {
                                                    blockTxIndices = emptyBag;
                                                    emptyBag = new ConcurrentBag<int>();
                                                }
                                                else
                                                    blockTxIndices = pruneData[spentTx.BlockIndex];

                                                blockTxIndices.Add(spentTx.TxIndex);
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        chainStateCursor.RollbackTransaction();
                                    }
                                }
                            });
                        }
                    });

                    // remove spent transactions from block storage
                    if (pruneData.Count > 0)
                    {
                        pruneBlocksStopwatch.Time(() =>
                        {
                            using (var blockTxesPruneQueue = new ConcurrentBlockingQueue<KeyValuePair<UInt256, IEnumerable<int>>>())
                            using (this.pruneBlockWorkers.Start(() =>
                            {
                                if (mode.HasFlag(PruningMode.BlockTxesPreserveMerkle))
                                    this.storageManager.BlockTxesStorage.PruneElements(blockTxesPruneQueue.GetConsumingEnumerable());
                                else
                                    this.storageManager.BlockTxesStorage.DeleteElements(blockTxesPruneQueue.GetConsumingEnumerable());
                            }))
                            {
                                foreach (var keyPair in pruneData)
                                {
                                    var confirmedBlockIndex = keyPair.Key;
                                    var confirmedBlockHash = chain.Blocks[confirmedBlockIndex].Hash;
                                    var spentTxIndices = keyPair.Value;

                                    blockTxesPruneQueue.Add(new KeyValuePair<UInt256, IEnumerable<int>>(confirmedBlockHash, spentTxIndices));
                                }

                                blockTxesPruneQueue.CompleteAdding();
                            }
                        });
                    }
                }

                if (mode.HasFlag(PruningMode.TxIndex) || mode.HasFlag(PruningMode.BlockSpentIndex))
                {
                    pruneIndexStopwatch.Time(() =>
                    {
                        using (var handle = this.storageManager.OpenChainStateCursor())
                        {
                            var chainStateCursor = handle.Item;

                            chainStateCursor.BeginTransaction();
                            var wasCommitted = false;
                            try
                            {
                                // TODO don't immediately remove list of spent txes per block from chain state,
                                //      use an additional safety buffer in case there was an issue pruning block
                                //      txes (e.g. didn't flush and crashed), keeping the information  will allow
                                //      the block txes pruning to be performed again
                                if (mode.HasFlag(PruningMode.BlockSpentIndex))
                                    chainStateCursor.TryRemoveBlockSpentTxes(pruneBlock.Height);

                                if (mode.HasFlag(PruningMode.TxIndex))
                                    foreach (var txHash in spentTxes)
                                        chainStateCursor.TryRemoveUnspentTx(txHash);

                                commitStopwatch.Time(() =>
                                {
                                    chainStateCursor.CommitTransaction();
                                    wasCommitted = true;
                                });
                            }
                            finally
                            {
                                if (!wasCommitted)
                                    chainStateCursor.RollbackTransaction();
                            }
                        }
                    });
                }
            }
            else //if (pruneBlock.Height > 0)
            {
                //TODO can't throw an exception unless the pruned chain is persisted
                //this.logger.Info("XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX: {0:#,##0}".Format2(pruneBlock.Height));
                //throw new InvalidOperationException();
                txCount = 0;
            }

            // track stats
            txCountMeasure.Tick(txCount);
            txRateMeasure.Tick((float)(txCount / totalStopwatch.Elapsed.TotalSeconds));
            gatherIndexDurationMeasure.Tick(gatherIndexStopwatch.Elapsed);
            pruneIndexDurationMeasure.Tick(pruneIndexStopwatch.Elapsed);
            pruneBlocksDurationMeasure.Tick(pruneBlocksStopwatch.Elapsed);
            commitDurationMeasure.Tick(commitStopwatch.Elapsed);
            totalDurationMeasure.Tick(totalStopwatch.Elapsed);
        }
    }
}
