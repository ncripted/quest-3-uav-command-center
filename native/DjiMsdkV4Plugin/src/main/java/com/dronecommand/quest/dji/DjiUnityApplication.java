package com.dronecommand.quest.dji;

import android.app.Application;
import android.content.Context;

public final class DjiUnityApplication extends Application {
    @Override
    protected void attachBaseContext(Context base) {
        super.attachBaseContext(base);
        com.cySdkyc.clx.Helper.install(this);
    }
}
