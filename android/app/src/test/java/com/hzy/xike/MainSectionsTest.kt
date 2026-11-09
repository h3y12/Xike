package com.hzy.xike

import android.content.Context
import android.view.View
import android.widget.EditText
import com.hzy.xike.core.AppConfig
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [28])
class MainSectionsTest {
    private val app get() = RuntimeEnvironment.getApplication()
    private val contentIds = listOf(R.id.sectionUsageContent, R.id.sectionCountdownContent,
        R.id.sectionAlarmContent, R.id.sectionScheduleContent, R.id.sectionRateContent,
        R.id.sectionTextContent, R.id.sectionLookContent)

    private val settingContentIds get() = contentIds.filter { it != R.id.sectionUsageContent }

    @Before fun setup() {
        app.getSharedPreferences(AppConfig.PREFS_NAME, Context.MODE_PRIVATE).edit().clear().commit()
        app.getSharedPreferences("settings_sections", Context.MODE_PRIVATE).edit().clear().commit()
        shadowOf(app).grantPermissions(app.packageName + ".DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION")
    }

    @Test fun firstVisitIsCompactAndEssentialGuidanceStaysVisible() {
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        try {
            val activity = controller.get()
            contentIds.forEach { id -> assertEquals(if (id == R.id.sectionAlarmContent) View.VISIBLE else View.GONE,
                activity.findViewById<View>(id).visibility) }
            assertEquals(View.VISIBLE, activity.findViewById<View>(R.id.tvUsageSummary).visibility)
            assertTrue(activity.findViewById<View>(R.id.btnToggleOverlay).isShown)
            // Stopping an active alarm must remain reachable even when its settings are folded.
            assertNotSame(activity.findViewById<View>(R.id.sectionAlarmContent),
                activity.findViewById<View>(R.id.alarmRingControls).parent)
        } finally { controller.pause().stop().destroy() }
    }

    @Test fun toolbarExpandsAllThenCollapsesAllAndChangesItsLabel() {
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        try {
            val activity = controller.get()
            val menu = androidx.appcompat.widget.PopupMenu(activity, activity.findViewById(R.id.toolbar)).menu
            activity.onCreateOptionsMenu(menu)
            activity.onPrepareOptionsMenu(menu)
            val item = menu.findItem(R.id.actionToggleSections)
            assertEquals("展开全部", item.title.toString())
            activity.onOptionsItemSelected(item)
            settingContentIds.forEach { assertEquals(View.VISIBLE, activity.findViewById<View>(it).visibility) }
            assertEquals(View.GONE, activity.findViewById<View>(R.id.sectionUsageContent).visibility)
            activity.onPrepareOptionsMenu(menu)
            assertEquals("收起全部", item.title.toString())
            activity.onOptionsItemSelected(item)
            contentIds.forEach { assertEquals(View.GONE, activity.findViewById<View>(it).visibility) }
            assertEquals(View.VISIBLE, activity.findViewById<View>(R.id.tvUsageSummary).visibility)
        } finally { controller.pause().stop().destroy() }
    }

    @Test fun foldingPreservesEditedSettingsAndSurvivesRecreation() {
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        try {
            var activity = controller.get()
            activity.findViewById<View>(R.id.sectionRateHeader).performClick()
            activity.findViewById<EditText>(R.id.etMinutes).setText("7")
            activity.findViewById<View>(R.id.sectionRateHeader).performClick()
            activity.findViewById<View>(R.id.sectionLookHeader).performClick()
            controller.recreate()
            activity = controller.get()
            assertEquals(View.GONE, activity.findViewById<View>(R.id.sectionRateContent).visibility)
            assertEquals(View.VISIBLE, activity.findViewById<View>(R.id.sectionLookContent).visibility)
            assertEquals("7", activity.findViewById<EditText>(R.id.etMinutes).text.toString())
            assertEquals(7, AppConfig(activity).minutesPerPoint)
        } finally { controller.pause().stop().destroy() }
    }

    @Test fun restoredExpandedSettingsShowCollapseEvenWithUsageFolded() {
        val prefs = app.getSharedPreferences("settings_sections", Context.MODE_PRIVATE)
        settingContentIds.forEach { prefs.edit().putBoolean(app.resources.getResourceEntryName(it), true).commit() }
        prefs.edit().putBoolean("sectionUsageContent", false).commit()
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        try {
            val activity = controller.get()
            val menu = androidx.appcompat.widget.PopupMenu(activity, activity.findViewById(R.id.toolbar)).menu
            activity.onCreateOptionsMenu(menu)
            val item = menu.findItem(R.id.actionToggleSections)
            assertEquals("收起全部", item.title.toString())
            activity.onOptionsItemSelected(item)
            assertEquals("展开全部", item.title.toString())
            settingContentIds.forEach { assertEquals(View.GONE, activity.findViewById<View>(it).visibility) }
            assertEquals(View.GONE, activity.findViewById<View>(R.id.sectionUsageContent).visibility)
        } finally { controller.pause().stop().destroy() }
    }

    @Test fun individualSettingTogglesUpdateTheToolbarImmediately() {
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        try {
            val activity = controller.get()
            val menu = androidx.appcompat.widget.PopupMenu(activity, activity.findViewById(R.id.toolbar)).menu
            activity.onCreateOptionsMenu(menu)
            val item = menu.findItem(R.id.actionToggleSections)
            activity.onOptionsItemSelected(item)
            assertEquals("收起全部", item.title.toString())
            activity.findViewById<View>(R.id.sectionRateHeader).performClick()
            assertEquals("展开全部", item.title.toString())
            activity.findViewById<View>(R.id.sectionRateHeader).performClick()
            assertEquals("收起全部", item.title.toString())
        } finally { controller.pause().stop().destroy() }
    }

    @Test fun usageStateIsIndependentOfTheGlobalButton() {
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        try {
            val activity = controller.get()
            val menu = androidx.appcompat.widget.PopupMenu(activity, activity.findViewById(R.id.toolbar)).menu
            activity.onCreateOptionsMenu(menu)
            val item = menu.findItem(R.id.actionToggleSections)
            activity.findViewById<View>(R.id.sectionUsageHeader).performClick()
            assertEquals("展开全部", item.title.toString())
            activity.onOptionsItemSelected(item)
            assertEquals("收起全部", item.title.toString())
            activity.onOptionsItemSelected(item)
            assertEquals(View.VISIBLE, activity.findViewById<View>(R.id.sectionUsageContent).visibility)
            activity.findViewById<View>(R.id.sectionUsageHeader).performClick()
            assertEquals("展开全部", item.title.toString())
        } finally { controller.pause().stop().destroy() }
    }
}
