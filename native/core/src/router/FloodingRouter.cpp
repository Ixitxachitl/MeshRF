// SPDX-License-Identifier: GPL-3.0-or-later
#include "mrf/router/FloodingRouter.h"

namespace mrf::router {

FloodingRouter::FloodingRouter(std::size_t dedup_capacity)
    : capacity_(dedup_capacity ? dedup_capacity : 1) {}

bool FloodingRouter::observe(const mac::PacketHeader& hdr) {
    Key k{hdr.sender, hdr.packet_id};
    if (ids_.contains(k)) return false;

    ids_.insert(k);
    order_.push_back(k);
    while (order_.size() > capacity_) {
        ids_.erase(order_.front());
        order_.pop_front();
    }
    return true;
}

} // namespace mrf::router
