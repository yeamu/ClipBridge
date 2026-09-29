package com.clipbridge

class RecentMessageIds(private val capacity: Int = 4096) {
    private val ids = linkedSetOf<String>()
    @Synchronized fun contains(id: String): Boolean = id in ids
    @Synchronized fun add(id: String): Boolean {
        if (!ids.add(id)) return false
        while (ids.size > capacity) ids.remove(ids.first())
        return true
    }
}
