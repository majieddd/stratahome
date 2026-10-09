# Updating Strata from StrataHome

StrataHome 0.3.1 includes **Server > Strata updates**. Automatic updates are enabled by default and can be switched off there. **Check now** and **Update now** work with automatic updates switched off. Update now rechecks official releases even if the last check found no update, and shows the installed and latest versions. The button says **Up to date** when current and remains usable to check again. Manual updates start as soon as Strata is idle; only automatic updates wait for the 30-second quiet period. Repeated clicks cannot restart the wait.

While StrataHome is running, it checks the official `Niko1221/Strata` stable releases on startup and every six hours. It installs a newer release after 30 seconds without requests, then starts the same model with the same memory mode. Reading, generation, queued requests, model loading and missing or stale metrics defer the update. A stopped server stays stopped. A server started outside StrataHome is only updated when you select **Update now**; automatic updates wait until StrataHome owns it.

The updater uses Strata's existing Python environment. For Git installs it fetches the official release tag and requires a clean, fast-forward source checkout. It refuses local tracked edits or divergent history. For ZIP installs it overlays the official tagged source while preserving model directories and run configurations. It downloads the installed Windows engine variants, verifies their published size and SHA-256, then runs the release's own `setup.py --update --yes` with those verified local archives. Locally compiled engines require Strata's build tools and are refused by this updater.

Model weights are not downloaded. The model identity, tokenizer, GGUF/pack paths, API connection settings and maximum context must survive the update. Strata's own compatible configuration upgrades and Python dependency updates are applied. After an active server restarts, StrataHome verifies its model and context and waits up to 15 minutes for it to become ready.

Before changing source or engine files, a recovery copy is saved under `%LOCALAPPDATA%\StrataHome\updates\backups\`. Setup failures restore the previous source, engine and run configurations. If the updated server fails its readiness checks, StrataHome restores that copy and attempts to start the previous engine. Python package changes in `.venv` are not rolled back. Recovery copies are retained; remove older ones yourself when you no longer need them. The Server log and `%LOCALAPPDATA%\StrataHome\logs\app.log` record progress and recovery paths.

Update checks require GitHub access. Downloads and Strata's dependency installer also need network access. Offline or rate-limited checks leave the existing installation usable and retry at the next scheduled check. A failed release is not automatically installed again in the same app session; select **Update now** to retry. Keep the app running until an installation finishes.

This updates **Strata**, not StrataHome itself. New launcher versions remain available from [StrataHome releases](https://github.com/majieddd/stratahome/releases).

## Developer checks

```
python -m unittest discover -s tools -p test_strata_update.py -v
build.cmd
dist\StrataHome.exe --updater-selftest
dist\StrataHome.exe --selftest
dist\StrataHome.exe --instance updater-smoke --no-start --uitest-ui
```

The offline Python tests exercise source overlays, Git fast-forward and rollback, download verification, model preservation, dirty checkouts and failures. `--updater-selftest` checks idle policy and Windows argument quoting. `--uitest-ui` checks the window and update controls without sending chat requests or clearing conversations. The GitHub workflow builds the launcher and runs the offline updater and policy checks on Windows.
