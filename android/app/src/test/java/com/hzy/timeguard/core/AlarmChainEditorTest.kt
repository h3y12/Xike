package com.hzy.timeguard.core

import org.junit.Assert.*
import org.junit.Test
import java.time.LocalTime

class AlarmChainEditorTest {
    private fun chain(book: AlarmBook) = book.addChain(
        AlarmChainSpec(startAt = LocalTime.of(23, 50), count = 3, intervalMinutes = 5), "起床", "a")

    @Test fun shiftingAcrossMidnightPreservesIdsIndicesAndOtherGroups() {
        val book = AlarmBook()
        val baseline = chain(book)
        val other = book.addChain(AlarmChainSpec(startAt = LocalTime.NOON, count = 2), "另一组", "b")
        assertTrue(book.mergeEditorChanges(baseline[0], baseline[0].copy(timeOfDay = LocalTime.of(0, 5)), baseline))
        assertEquals(listOf("00:05", "00:10", "00:15"), baseline.map { book.find(it.id)!!.timeText })
        assertEquals(listOf(1, 2, 3), baseline.map { book.find(it.id)!!.chainIndex })
        assertEquals(other, other.map { book.find(it.id)!! })
    }

    @Test fun consumedMembersStayRemovedAndRuntimeDisableIsPreserved() {
        val book = AlarmBook()
        val baseline = chain(book)
        book.remove(baseline[0].id)
        book.setChainEnabled("a", false)
        assertTrue(book.mergeEditorChanges(baseline[1], baseline[1].copy(timeOfDay = LocalTime.of(7, 5)), baseline))
        assertNull(book.find(baseline[0].id))
        assertEquals(listOf("07:05", "07:10"), baseline.drop(1).map { book.find(it.id)!!.timeText })
        assertTrue(book.all.all { !it.enabled })
        assertFalse(book.mergeEditorChanges(baseline[0], baseline[0].copy(label = "不可复活"), baseline))
    }

    @Test fun staleTimeEditCannotPartiallyShiftAChainChangedInTheBackground() {
        val book = AlarmBook()
        val baseline = chain(book)
        book.add(baseline[1].copy(timeOfDay = LocalTime.of(23, 58)))
        assertTrue(book.mergeEditorChanges(baseline[0], baseline[0].copy(timeOfDay = LocalTime.of(6, 0), label = "新名字"), baseline))
        assertEquals(listOf("23:50", "23:58", "00:00"), baseline.map { book.find(it.id)!!.timeText })
        assertTrue(book.all.all { it.label == "新名字" })
    }

    @Test fun ordinaryAlarmEditDoesNotChangeTheChain() {
        val book = AlarmBook()
        val baseline = chain(book)
        val ordinary = book.addManual(LocalTime.NOON, "普通", AlarmRepeat.DAILY, emptySet(), false)
        assertTrue(book.mergeEditorChanges(ordinary, ordinary.copy(timeOfDay = LocalTime.of(13, 0))))
        assertEquals("13:00", book.find(ordinary.id)!!.timeText)
        assertEquals(baseline, baseline.map { book.find(it.id)!! })
    }
    @Test fun resizingDisabledChainPreservesDisabledStateAndOtherGroups() {
        val book = AlarmBook()
        chain(book)
        book.setChainEnabled("a", false)
        val baseline = book.chains().single().second
        val other = book.addChain(AlarmChainSpec(count = 2), "另一组", "b")
        assertTrue(book.resizeChainFromEditor(baseline[1], baseline[1], baseline, 10, 5))
        val resized = book.chains().single { it.first == "a" }.second
        assertEquals(listOf("23:50", "00:00", "00:10", "00:20", "00:30"), resized.map { it.timeText })
        assertTrue(resized.all { !it.enabled })
        assertEquals(other, other.map { book.find(it.id)!! })
    }

    @Test fun oldRecordsWithoutIntervalRemainReadable() {
        val old = "old\u000106:30\u0001起床\u00011\u0001daily\u0001\u00010\u0001group\u00011"
        val record = AlarmStore.parse(old).single()
        assertEquals(0, record.chainIntervalMinutes)
        assertEquals("group", record.chain)
        val updated = record.copy(chainIntervalMinutes = 7)
        assertEquals(updated, AlarmStore.parse(AlarmStore.serialize(listOf(updated))).single())
    }
    @Test fun controlCharactersInLabelCannotSplitRecordsOrShiftFields() {
        val records = listOf(
            AlarmStore.Record(id = "a", at = "06:30", label = "早\u0001起\n闹\r钟", chain = "group", chainIndex = 1),
            AlarmStore.Record(id = "b", at = "07:00", label = "普通"),
        )
        val restored = AlarmStore.parse(AlarmStore.serialize(records))
        assertEquals(2, restored.size)
        assertEquals("早 起 闹 钟", restored[0].label)
        assertEquals("group", restored[0].chain)
        assertEquals(1, restored[0].chainIndex)
        assertEquals(records[1], restored[1])
    }
}
