package com.hzy.timeguard

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioManager
import android.media.RingtoneManager
import android.net.Uri
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import org.robolectric.shadows.ShadowMediaPlayer
import org.robolectric.shadows.util.DataSource
import java.io.IOException

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [28])
class AlarmAudioTest {
    private val app get() = RuntimeEnvironment.getApplication()
    private val players = mutableListOf<ShadowMediaPlayer>()

    @Before fun setup() {
        shadowOf(app).grantPermissions("com.hzy.timeguard.DYNAMIC_RECEIVER_NOT_EXPORTED_PERMISSION", "android.permission.WAKE_LOCK")
        ShadowMediaPlayer.setMediaInfoProvider { ShadowMediaPlayer.MediaInfo(2000, 0) }
        ShadowMediaPlayer.setCreateListener { _, shadow -> players += shadow }
    }

    private fun assertAlarmChannel(player: ShadowMediaPlayer) {
        assertEquals(ShadowMediaPlayer.State.STARTED, player.state)
        assertEquals(AudioManager.STREAM_ALARM, player.theAudioStreamType)
        assertEquals(AudioAttributes.USAGE_ALARM, player.audioAttributes!!.usage)
        assertEquals(AudioManager.STREAM_ALARM, player.audioAttributes!!.volumeControlStream)
    }

    @Test fun systemToneUsesAlarmChannelWithMediaVolumeMuted() {
        val audio = app.getSystemService(Context.AUDIO_SERVICE) as AudioManager
        audio.setStreamVolume(AudioManager.STREAM_MUSIC, 0, 0)
        audio.setStreamVolume(AudioManager.STREAM_ALARM, 2, 0)
        val alarmVolume = audio.getStreamVolume(AudioManager.STREAM_ALARM)
        val output = AlarmPlayback(app)
        try {
            output.startSound()
            assertEquals(1, players.size)
            assertAlarmChannel(players.single())
            assertEquals(0, audio.getStreamVolume(AudioManager.STREAM_MUSIC))
            assertEquals(alarmVolume, audio.getStreamVolume(AudioManager.STREAM_ALARM))
        } finally { output.stopSound() }
        assertEquals(ShadowMediaPlayer.State.END, players.single().state)
    }

    @Test fun fallbackToneAlsoUsesAlarmChannelAfterSystemToneFails() {
        val default = RingtoneManager.getDefaultUri(RingtoneManager.TYPE_ALARM)
        ShadowMediaPlayer.addException(DataSource.toDataSource(app, default), IOException("Unavailable alarm tone"))
        val output = AlarmPlayback(app)
        try {
            output.startSound()
            assertEquals(2, players.size)
            assertEquals(ShadowMediaPlayer.State.END, players.first().state)
            assertAlarmChannel(players.last())
            val fallback = Uri.parse("android.resource://${app.packageName}/${R.raw.alarm_fallback}")
            assertEquals(DataSource.toDataSource(app, fallback), players.last().dataSource)
        } finally { output.stopSound() }
    }

    @Test fun mainScreenVolumeKeysTargetAlarmVolume() {
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        try {
            assertEquals(AudioManager.STREAM_ALARM, controller.get().volumeControlStream)
        } finally { controller.pause().stop().destroy() }
    }
}
