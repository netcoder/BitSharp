﻿using BitSharp.Common;
using BitSharp.Core.Domain;
using BitSharp.Core.Rules;
using BitSharp.Core.Storage;
using NLog;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace BitSharp.Core.Builders
{
    public class BlockReplayer : IDisposable
    {
        private readonly Logger logger = LogManager.GetCurrentClassLogger();

        private readonly CoreStorage coreStorage;
        private readonly IBlockchainRules rules;

        private readonly UtxoReplayer pendingTxLoader;
        private readonly TxLoader txLoader;
        private readonly ParallelConsumer<LoadedTx> txSorter;

        public BlockReplayer(CoreStorage coreStorage, IBlockchainRules rules)
        {
            this.coreStorage = coreStorage;
            this.rules = rules;

            // thread count for i/o task (TxLoader)
            var ioThreadCount = 4;

            this.pendingTxLoader = new UtxoReplayer("BlockReplayer", coreStorage, ioThreadCount);
            this.txLoader = new TxLoader("BlockReplayer", null, coreStorage, ioThreadCount);
            this.txSorter = new ParallelConsumer<LoadedTx>("BlockReplayer", 1);
        }

        public void Dispose()
        {
            this.pendingTxLoader.Dispose();
            this.txLoader.Dispose();
            this.txSorter.Dispose();
        }

        public IEnumerable<LoadedTx> ReplayBlock(IChainState chainState, UInt256 blockHash, bool replayForward)
        {
            var replayTxes = new List<LoadedTx>();
            ReplayBlock(chainState, blockHash, replayForward, loadedTx => replayTxes.Add(loadedTx));
            return replayTxes;
        }

        public void ReplayBlock(IChainState chainState, UInt256 blockHash, bool replayForward, Action<LoadedTx> replayAction)
        {
            var replayBlock = this.coreStorage.GetChainedHeader(blockHash);
            if (replayBlock == null)
                throw new MissingDataException(blockHash);

            this.pendingTxLoader.ReplayCalculateUtxo(chainState, replayBlock, replayForward,
                loadingTxes =>
                {
                    this.txLoader.LoadTxes(loadingTxes,
                        loadedTxes =>
                        {
                            using (var sortedTxes = new ConcurrentBlockingQueue<LoadedTx>())
                            using (StartTxSorter(replayBlock, loadedTxes, sortedTxes))
                            {
                                var replayTxes = sortedTxes.GetConsumingEnumerable();
                                //TODO Reverse() here means everything must be loaded first, the tx sorter should handle this instead
                                if (!replayForward)
                                    replayTxes = replayTxes.Reverse();

                                foreach (var tx in replayTxes)
                                    replayAction(tx);
                            }
                        });
                });
        }

        private IDisposable StartTxSorter(ChainedHeader replayBlock, ConcurrentBlockingQueue<LoadedTx> loadedTxes, ConcurrentBlockingQueue<LoadedTx> sortedTxes)
        {
            // txSorter will only have a single consumer thread, so SortedList is safe to use
            var pendingSortedTxes = new SortedList<int, LoadedTx>();

            // keep track of which tx is the next one in order
            var nextTxIndex = 0;

            return this.txSorter.Start(loadedTxes,
                loadedTx =>
                {
                    // store loaded tx
                    pendingSortedTxes.Add(loadedTx.TxIndex, loadedTx);

                    // dequeue any available loaded txes that are in order
                    while (pendingSortedTxes.Count > 0 && pendingSortedTxes.Keys[0] == nextTxIndex)
                    {
                        sortedTxes.Add(pendingSortedTxes.Values[0]);
                        pendingSortedTxes.RemoveAt(0);
                        nextTxIndex++;
                    }
                },
                e =>
                {
                    // ensure no txes were left unsorted
                    if (pendingSortedTxes.Count > 0)
                        throw new InvalidOperationException();

                    sortedTxes.CompleteAdding();
                });
        }
    }
}
