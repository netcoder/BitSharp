﻿using System.Collections.Immutable;

namespace BitSharp.Network.Domain
{
    public class InventoryPayload
    {
        public readonly ImmutableArray<InventoryVector> InventoryVectors;

        public InventoryPayload(ImmutableArray<InventoryVector> InventoryVectors)
        {
            this.InventoryVectors = InventoryVectors;
        }
    }
}
