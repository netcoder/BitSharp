﻿using BitSharp.Common;
using BitSharp.Common.ExtensionMethods;
using BitSharp.Core.Domain;
using BitSharp.Core.ExtensionMethods;
using BitSharp.Core.Builders;
using BitSharp.Core.Rules;
using BitSharp.Core.Storage;
using NLog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace BitSharp.Core.Workers
{
    internal class PruningWorker : Worker
    {
        private readonly Logger logger;
        private readonly ICoreDaemon coreDaemon;
        private readonly IStorageManager storageManager;
        private readonly ChainStateWorker chainStateWorker;

        private readonly ParallelConsumer<KeyValuePair<int, List<int>>> blockTxesPruner;

        //TODO
        private ChainBuilder prunedChain;

        public PruningWorker(WorkerConfig workerConfig, ICoreDaemon coreDaemon, IStorageManager storageManager, ChainStateWorker chainStateWorker, Logger logger)
            : base("PruningWorker", workerConfig.initialNotify, workerConfig.minIdleTime, workerConfig.maxIdleTime, logger)
        {
            this.logger = logger;
            this.coreDaemon = coreDaemon;
            this.storageManager = storageManager;
            this.chainStateWorker = chainStateWorker;

            this.prunedChain = new ChainBuilder();
            this.Mode = PruningMode.None;

            var txesPruneThreadCount = 4;
            this.blockTxesPruner = new ParallelConsumer<KeyValuePair<int, List<int>>>("PruningWorker.BlockTxesPruner", txesPruneThreadCount, logger);
        }

        protected override void SubDispose()
        {
            this.blockTxesPruner.Dispose();
        }

        public PruningMode Mode { get; set; }

        public int PrunableHeight { get; set; }

        protected override void WorkAction()
        {
            // check if pruning is turned off
            if (this.Mode == PruningMode.None)
                return;

            using (var handle = this.storageManager.OpenChainStateCursor())
            {
                var chainStateCursor = handle.Item;
                var stopwatch = Stopwatch.StartNew();

                // get the current processed chain
                var processedChain = this.coreDaemon.CurrentChain;

                // ensure chain state is flushed before pruning
                chainStateCursor.Flush();

                // begin the pruning transaction
                chainStateCursor.BeginTransaction();

                // navigate from the current pruned chain towards the current processed chain
                foreach (var pathElement in this.prunedChain.ToImmutable().NavigateTowards(processedChain))
                {
                    // cooperative loop
                    if (!this.IsStarted)
                        break;

                    // check if pruning is turned off
                    var mode = this.Mode;
                    if (mode == PruningMode.None)
                        break;

                    // get candidate block to be pruned
                    var direction = pathElement.Item1;
                    var chainedHeader = pathElement.Item2;

                    // determine maximum safe pruning height, based on a buffer distance from the processed chain height
                    var blocksPerDay = 144;
                    var pruneBuffer = blocksPerDay * 7;
                    var maxHeight = processedChain.Height - pruneBuffer;
                    //TODO
                    maxHeight = Math.Min(maxHeight, this.PrunableHeight);

                    // check if this block is safe to prune
                    if (chainedHeader.Height > maxHeight)
                    {
                        //TODO better way to block chain state worker when pruning is behind
                        if (this.chainStateWorker != null)
                            this.chainStateWorker.Start();
                        break;
                    }

                    if (direction > 0)
                    {
                        // prune the block
                        this.PruneBlock(mode, processedChain, chainedHeader, chainStateCursor);

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

                    // limit how long pruning transaction is kept open
                    if (stopwatch.Elapsed > TimeSpan.FromSeconds(15))
                    {
                        //TODO better way to block chain state worker when pruning is behind
                        this.logger.Info("Pausing chain state processing, pruning is lagging.");
                        if (this.chainStateWorker != null)
                            this.chainStateWorker.Stop();

                        this.ForceWork();
                        break;
                    }
                }

                // blocks must be flushed as the pruning information has been removed from the chain state
                // if the system crashed and the pruned chain state was persisted while the pruned blocks were not,
                // the information to prune them again would be lost
                this.storageManager.BlockTxesStorage.Flush();

                // commit pruned chain state
                // flush is not needed here, at worst pruning will be performed again against already pruned transactions
                chainStateCursor.CommitTransaction();

                //this.logger.Info("Finished round of pruning.");

                //TODO add periodic stats logging like ChainStateBuilder has
            }
        }

        private void PruneBlock(PruningMode mode, Chain chain, ChainedHeader pruneBlock, IChainStateCursor chainStateCursor)
        {
            //TODO the replay information about blocks that have been rolled back also needs to be pruned (UnmintedTx)

            // dictionary to keep track of spent transactions against their block
            var pruneData = new SortedDictionary<int, List<int>>();

            var txCount = 0;

            // retrieve the spent txes for this block
            IImmutableList<SpentTx> spentTxes;
            if (chainStateCursor.TryGetBlockSpentTxes(pruneBlock.Height, out spentTxes))
            {
                // remove each spent tx
                foreach (var spentTx in spentTxes)
                {
                    // cooperative loop
                    this.ThrowIfCancelled();

                    txCount++;

                    // remove spent tx from chain state
                    chainStateCursor.TryRemoveUnspentTx(spentTx.TxHash);

                    // queue up spent tx to be pruned from block txes
                    if (mode == PruningMode.ReplayAndRollbackAndTxes)
                    {
                        if (!pruneData.ContainsKey(spentTx.ConfirmedBlockIndex))
                            pruneData[spentTx.ConfirmedBlockIndex] = new List<int>();
                        pruneData[spentTx.ConfirmedBlockIndex].Add(spentTx.TxIndex);
                    }
                }

                // remove list of spent txes for this block
                if (mode == PruningMode.ReplayAndRollback || mode == PruningMode.ReplayAndRollbackAndTxes)
                    chainStateCursor.TryRemoveBlockSpentTxes(pruneBlock.Height);

                // remove spent transactions from block storage
                if (mode == PruningMode.ReplayAndRollbackAndTxes && pruneData.Count > 0)
                {
                    using (this.blockTxesPruner.Start(pruneData,
                        keyPair =>
                        {
                            // cooperative loop
                            this.ThrowIfCancelled();

                            var confirmedBlockIndex = keyPair.Key;
                            var confirmedBlockHash = chain.Blocks[confirmedBlockIndex].Hash;
                            var spentTxIndices = keyPair.Value;

                            this.storageManager.BlockTxesStorage.PruneElements(confirmedBlockHash, spentTxIndices);
                        },
                        () => { }))
                    {
                        this.blockTxesPruner.WaitToComplete();
                    }
                }

                if (pruneBlock.Height % 20 == 0)
                    this.logger.Info("{0:#,##0}: {1,10:#,##0}, {2,10:#,##0}".Format2(pruneBlock.Height, pruneData.Count, txCount));
            }
            else if (pruneBlock.Height > 0)
            {
                //TODO can't throw an exception unless the pruned chain is persisted
                //this.logger.Info("XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX: {0:#,##0}".Format2(pruneBlock.Height));
                //throw new InvalidOperationException();
            }
        }
    }
}
