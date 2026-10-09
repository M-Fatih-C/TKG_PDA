package com.logirobotic.tkg_pda

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import io.flutter.plugin.common.EventChannel
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.atomic.AtomicBoolean

class TkgNotificationService : Service() {
    private val running = AtomicBoolean(false)
    private var worker: Thread? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        startInForeground()
        val stored = TkgCredentialStore.load(this)
        if (stored == null) {
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf()
            return START_NOT_STICKY
        }
        if (running.compareAndSet(false, true)) {
            worker = Thread({ poll(stored) }, "tkg-notifications").also { it.start() }
        }
        return START_STICKY
    }

    override fun onDestroy() {
        running.set(false)
        worker?.interrupt()
        super.onDestroy()
    }

    private fun poll(stored: TkgCredentialStore.Stored) {
        var backoffMs = 1000L
        while (running.get() && !Thread.currentThread().isInterrupted) {
            try {
                when (val response = getNext(stored)) {
                    is PollResult.Event -> {
                        backoffMs = 1000L
                        present(response)
                        acknowledge(stored, response.id)
                    }
                    PollResult.Empty -> backoffMs = 1000L
                    PollResult.Rejected -> {
                        running.set(false)
                        stopForeground(STOP_FOREGROUND_REMOVE)
                        stopSelf()
                        return
                    }
                    is PollResult.Retry -> sleep(backoffMs).also { backoffMs = (backoffMs * 2).coerceAtMost(30_000L) }
                }
            } catch (_: InterruptedException) {
                return
            } catch (_: Exception) {
                sleep(backoffMs)
                backoffMs = (backoffMs * 2).coerceAtMost(30_000L)
            }
        }
    }

    private fun present(event: PollResult.Event) {
        val seen = TkgCredentialStore.hasSeen(this, event.eventKey)
        if (!seen) {
            TkgCredentialStore.remember(this, event.eventKey)
            val deliveredToUi = inForeground.get() && Bridge.emit(event.data)
            if (!deliveredToUi) {
                showDelivery(event.title, event.body)
            }
        }
    }

    private fun showDelivery(title: String, body: String) {
        val manager = getSystemService(NotificationManager::class.java)
        val launch = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        val notification = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            Notification.Builder(this, DELIVERY_CHANNEL)
        } else {
            @Suppress("DEPRECATION")
            Notification.Builder(this)
        }
            .setContentTitle(title)
            .setContentText(body)
            .setSmallIcon(applicationInfo.icon)
            .setAutoCancel(true)
            .setContentIntent(launch)
            .build()
        manager.notify(eventNotificationId(title, body), notification)
    }

    private fun getNext(stored: TkgCredentialStore.Stored): PollResult {
        val connection = open(stored, "/api/v1/device-notifications/next", "GET")
        connection.readTimeout = 40_000
        return read(connection)
    }

    private fun acknowledge(stored: TkgCredentialStore.Stored, id: String) {
        val connection = open(stored, "/api/v1/device-notifications/$id/ack", "POST")
        connection.doOutput = true
        connection.outputStream.use { it.write(ByteArray(0)) }
        connection.responseCode
        connection.disconnect()
    }

    private fun open(stored: TkgCredentialStore.Stored, path: String, method: String): HttpURLConnection {
        val base = stored.baseUrl.trimEnd('/')
        val connection = URL(base + path).openConnection() as HttpURLConnection
        connection.requestMethod = method
        connection.connectTimeout = 15_000
        connection.readTimeout = 20_000
        connection.setRequestProperty("Accept", "application/json")
        connection.setRequestProperty("X-TKG-Notification-Credential", stored.credential)
        connection.useCaches = false
        return connection
    }

    private fun read(connection: HttpURLConnection): PollResult {
        return try {
            val code = connection.responseCode
            when (code) {
                204 -> PollResult.Empty
                401, 403 -> PollResult.Rejected
                200 -> {
                    val text = connection.inputStream.bufferedReader().use { it.readText() }
                    val json = JSONObject(text)
                    val data = json.optJSONObject("data") ?: JSONObject()
                    val mapped = mutableMapOf<String, String>()
                    val keys = data.keys()
                    while (keys.hasNext()) {
                        val key = keys.next()
                        mapped[key] = data.optString(key)
                    }
                    PollResult.Event(
                        json.optString("id"),
                        json.optString("eventKey"),
                        json.optString("title"),
                        json.optString("body"),
                        mapped
                    )
                }
                else -> PollResult.Retry
            }
        } finally {
            connection.disconnect()
        }
    }

    private fun startInForeground() {
        val manager = getSystemService(NotificationManager::class.java)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            manager.createNotificationChannel(
                NotificationChannel(SERVICE_CHANNEL, "TKG bağlantı", NotificationManager.IMPORTANCE_LOW)
            )
            manager.createNotificationChannel(
                NotificationChannel(DELIVERY_CHANNEL, "TKG Depo Bildirimleri", NotificationManager.IMPORTANCE_HIGH)
            )
        }
        val notification = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            Notification.Builder(this, SERVICE_CHANNEL)
        } else {
            @Suppress("DEPRECATION")
            Notification.Builder(this)
        }
            .setContentTitle("TKG depo bildirimleri açık")
            .setSmallIcon(applicationInfo.icon)
            .setOngoing(true)
            .build()
        if (Build.VERSION.SDK_INT >= 34) {
            startForeground(SERVICE_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_REMOTE_MESSAGING)
        } else {
            startForeground(SERVICE_ID, notification)
        }
    }

    private fun sleep(millis: Long) {
        try {
            Thread.sleep(millis)
        } catch (_: InterruptedException) {
            Thread.currentThread().interrupt()
        }
    }

    private fun eventNotificationId(title: String, body: String): Int =
        (title + body).hashCode() and 0x7fffffff

    private sealed interface PollResult {
        data object Empty : PollResult
        data object Rejected : PollResult
        data object Retry : PollResult
        data class Event(
            val id: String,
            val eventKey: String,
            val title: String,
            val body: String,
            val data: Map<String, String>
        ) : PollResult
    }

    companion object {
        const val SERVICE_CHANNEL = "tkg_notification_service"
        const val DELIVERY_CHANNEL = "tkg_warehouse_deliveries"
        private const val SERVICE_ID = 4109
        val inForeground = AtomicBoolean(false)

        fun start(context: Context) {
            val intent = Intent(context, TkgNotificationService::class.java)
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(intent)
            } else {
                context.startService(intent)
            }
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, TkgNotificationService::class.java))
        }
    }

    object Bridge {
        @Volatile
        var sink: EventChannel.EventSink? = null

        fun emit(data: Map<String, String>): Boolean {
            val current = sink ?: return false
            Handler(Looper.getMainLooper()).post { current.success(data) }
            return true
        }
    }
}
