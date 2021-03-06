﻿using System.Collections.Immutable;

namespace BitSharp.Network.Domain
{
    public class AddressPayload
    {
        public readonly ImmutableArray<NetworkAddressWithTime> NetworkAddresses;

        public AddressPayload(ImmutableArray<NetworkAddressWithTime> NetworkAddresses)
        {
            this.NetworkAddresses = NetworkAddresses;
        }
    }
}
