package com.weathervr;

import android.content.Intent;
import android.os.Bundle;

import com.unity3d.player.UnityPlayer;
import com.unity3d.player.UnityPlayerActivity;

/**
 * PICO can keep immersive activities resumed while their spatial container is
 * hidden. Forward Android's real resume/new-intent lifecycle to Unity so every
 * app-icon launch replays the globe.
 */
public class WeatherVRActivity extends UnityPlayerActivity {
    private boolean hasResumedOnce;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        hasResumedOnce = false;
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (hasResumedOnce) {
            notifyUnityOfForegroundLaunch();
        }
        hasResumedOnce = true;
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        notifyUnityOfForegroundLaunch();
    }

    private void notifyUnityOfForegroundLaunch() {
        UnityPlayer.UnitySendMessage(
            "Weather Scene Bootstrap",
            "OnAndroidForegroundLaunch",
            "");
    }
}
