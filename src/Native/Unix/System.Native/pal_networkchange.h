// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#pragma once

#include "pal_compiler.h"

BEGIN_EXTERN_C

#include "pal_types.h"
#include <linux/netlink.h>

enum class NetworkChangeKind : int32_t
{
    None = -1,
    AddressAdded = 0,
    AddressRemoved = 1,
    LinkAdded = 2,
    LinkRemoved = 3,
    AvailabilityChanged = 4,
};

typedef void (*NetworkChangeEvent)(int32_t sock, NetworkChangeKind notificationKind);

DLLEXPORT void SystemNative_ReadEvents(int32_t sock, NetworkChangeEvent onNetworkChange);
NetworkChangeKind ReadNewLinkMessage(nlmsghdr* hdr);

END_EXTERN_C
