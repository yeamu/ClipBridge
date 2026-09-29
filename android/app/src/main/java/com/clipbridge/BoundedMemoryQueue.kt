package com.clipbridge

class BoundedMemoryQueue<T : Any>(private val measure: (T) -> Long,
    private val maxItems: Int = 20, private val budget: Long = 64L * 1024 * 1024,
    private val maxSingle: Long = 320L * 1024 * 1024) {
    private val items = ArrayDeque<Pair<T, Long>>()
    private var bytes = 0L
    @Synchronized fun canAccept(size: Long): Boolean =
        size <= maxSingle && items.size < maxItems && (items.isEmpty() || bytes + size <= budget)
    @Synchronized fun add(item: T): Boolean {
        val size = measure(item)
        if (size > maxSingle || items.size >= maxItems || (items.isNotEmpty() && bytes + size > budget)) return false
        items.addLast(item to size); bytes += size
        return true
    }
    @Synchronized fun peek(): T? = items.firstOrNull()?.first
    @Synchronized fun complete(item: T): Boolean {
        val current = items.firstOrNull() ?: return false
        if (current.first !== item) return false
        items.removeFirst(); bytes -= current.second
        return true
    }
    @Synchronized fun clear() { items.clear(); bytes = 0 }
}
