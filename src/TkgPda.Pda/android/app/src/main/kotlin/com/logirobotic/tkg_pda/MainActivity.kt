package com.logirobotic.tkg_pda

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.EventChannel
import io.flutter.plugin.common.MethodChannel

class MainActivity : FlutterActivity() {
    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, CHANNEL)
            .setMethodCallHandler { call, result ->
                when (call.method) {
                    "start" -> {
                        val credential = call.argument<String>("credential").orEmpty()
                        val baseUrl = call.argument<String>("baseUrl").orEmpty()
                        val registrationId = call.argument<String>("registrationId").orEmpty()
                        if (credential.isBlank() || baseUrl.isBlank()) {
                            result.error("invalid_arguments", "Missing notification registration.", null)
                            return@setMethodCallHandler
                        }
                        requestNotificationPermission()
                        TkgCredentialStore.save(this, credential, baseUrl, registrationId)
                        TkgNotificationService.start(this)
                        result.success(null)
                    }
                    "stop" -> {
                        TkgNotificationService.stop(this)
                        TkgCredentialStore.clear(this)
                        result.success(null)
                    }
                    else -> result.notImplemented()
                }
            }
        EventChannel(flutterEngine.dartExecutor.binaryMessenger, EVENTS)
            .setStreamHandler(object : EventChannel.StreamHandler {
                override fun onListen(arguments: Any?, events: EventChannel.EventSink?) {
                    TkgNotificationService.Bridge.sink = events
                }

                override fun onCancel(arguments: Any?) {
                    TkgNotificationService.Bridge.sink = null
                }
            })
    }

    override fun onResume() {
        super.onResume()
        TkgNotificationService.inForeground.set(true)
    }

    override fun onPause() {
        TkgNotificationService.inForeground.set(false)
        super.onPause()
    }

    private fun requestNotificationPermission() {
        if (Build.VERSION.SDK_INT < 33) return
        if (checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) {
            return
        }
        requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 4109)
    }

    companion object {
        private const val CHANNEL = "com.logirobotic.tkg_pda/device_notifications"
        private const val EVENTS = "com.logirobotic.tkg_pda/device_notifications_events"
    }
}
