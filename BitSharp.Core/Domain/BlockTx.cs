﻿using BitSharp.Common;

namespace BitSharp.Core.Domain
{
    public class BlockTx : MerkleTreeNode
    {
        private readonly Transaction transaction;

        public BlockTx(int index, int depth, UInt256 hash, bool pruned, Transaction transaction)
            : base(index, depth, hash, pruned)
        {
            this.transaction = transaction;
        }

        //TODO only used by tests
        public BlockTx(int txIndex, Transaction tx)
            : this(txIndex, 0, tx.Hash, false, tx)
        { }

        public bool IsCoinbase { get { return this.Index == 0; } }

        public Transaction Transaction { get { return this.transaction; } }
    }
}
