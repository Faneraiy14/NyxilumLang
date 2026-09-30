package nyx.bridge;

import android.app.job.JobParameters;
import android.app.job.JobService;

// Фонова подія "sync" раз на ~15 хв (JobScheduler, без Firebase)
public final class SyncJob extends JobService {
    @Override
    public boolean onStartJob(final JobParameters params) {
        new Thread(() -> {
            NxBridge.runOnce(getApplicationContext(), "sync", "");
            jobFinished(params, false);
        }).start();
        return true;
    }

    @Override
    public boolean onStopJob(JobParameters params) {
        return true;
    }
}
