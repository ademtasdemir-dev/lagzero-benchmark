using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Lagzero;

/// <summary>Writes a polling2-compatible JSON log (test_info / raw_data / cleaned_data) plus a results block.</summary>
public static class LogWriter
{
    public static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public static string DefaultDirectory()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Logs");
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".write_test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return dir;
        }
        catch
        {
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LAGZERO Logs");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string DefaultFileName(DateTime t) => $"lagzero_log_{t:yyyyMMdd_HHmmss}.json";

    public static void Write(string path, PollingResult? poll, DriftResult? drift, ControllerInfo controller)
    {
        using var fs = File.Create(path);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
        var date = poll?.Capture.StartedAt ?? drift?.StartedAt ?? DateTime.Now;

        w.WriteStartObject();

        w.WriteStartObject("test_info");
        w.WriteString("program", "LAGZERO Controller Benchmark");
        w.WriteString("version", AppVersion);
        w.WriteString("joystick_name", $"({controller.Name})");
        w.WriteString("detected_mode", "XInput");
        w.WriteNumber("xinput_slot", controller.Index + 1);
        w.WriteString("vid_pid", controller.VidPid);
        w.WriteString("test_date", date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        w.WriteString("method", "XInputGetState busy loop, new packet = dwPacketNumber change, QPC timestamps");
        if (poll != null)
        {
            var f = poll.Filter;
            w.WriteStartObject("filter_settings");
            w.WriteNumber("window_size", f.WindowSize);
            w.WriteNumber("multiplier", f.Multiplier);
            w.WriteNumber("sync_tolerance_pct", f.SyncTolerancePct);
            w.WriteNumber("warmup_threshold_ms", f.WarmupThresholdMs);
            w.WriteNumber("threshold_floor_ms", f.ThresholdFloorMs);
            w.WriteEndObject();
            w.WriteNumber("qpc_frequency_hz", System.Diagnostics.Stopwatch.Frequency);
            if (!double.IsNaN(poll.Capture.TimerResolutionMs))
                w.WriteNumber("timer_resolution_ms", Math.Round(poll.Capture.TimerResolutionMs, 4));
        }
        w.WriteEndObject();

        if (poll != null)
        {
            var c = poll.Capture;
            w.WriteStartObject("results");
            w.WriteNumber("rate_class_hz", poll.RateClass);
            WriteStats(w, "filtered", poll.Filtered);
            WriteStats(w, "unfiltered", poll.Unfiltered);
            w.WriteNumber("outliers_removed", poll.Outliers);
            w.WriteNumber("skipped_packets", c.SkippedPackets);
            w.WriteNumber("loop_reads", c.Polls);
            w.WriteNumber("loop_reads_per_sec", Math.Round(c.PollsPerSecond));
            w.WriteNumber("duration_s", Math.Round(c.DurationSec, 4));
            if (c.Error != null) w.WriteString("error", c.Error);
            if (c.Cancelled) w.WriteBoolean("cancelled", true);
            w.WriteEndObject();
        }

        if (drift != null)
        {
            w.WriteStartObject("drift_test");
            w.WriteString("test_date", drift.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            w.WriteNumber("duration_s", Math.Round(drift.Seconds, 3));
            w.WriteNumber("samples", drift.Samples);
            WriteStick(w, "left_stick", drift.Left);
            WriteStick(w, "right_stick", drift.Right);
            w.WriteEndObject();
        }

        if (poll != null)
        {
            var d = poll.IntervalsMs;
            w.WriteNumber("raw_data_count", d.Length);
            w.WriteNumber("cleaned_data_count", poll.Filtered.N);
            w.WriteStartArray("raw_data");
            for (int i = 0; i < d.Length; i++) WriteSample(w, poll, i);
            w.WriteEndArray();
            w.WriteStartArray("cleaned_data");
            for (int i = 0; i < d.Length; i++) if (poll.Keep[i]) WriteSample(w, poll, i);
            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    private static void WriteStats(Utf8JsonWriter w, string name, IntervalStats s)
    {
        w.WriteStartObject(name);
        w.WriteNumber("count", s.N);
        w.WriteNumber("hz_median", Math.Round(s.HzMedian, 1));
        w.WriteNumber("hz_mean", Math.Round(s.HzMean, 1));
        w.WriteNumber("median_ms", Math.Round(s.MedianMs, 4));
        w.WriteNumber("mean_ms", Math.Round(s.MeanMs, 4));
        w.WriteNumber("min_ms", Math.Round(s.MinMs, 4));
        w.WriteNumber("max_ms", Math.Round(s.MaxMs, 4));
        w.WriteNumber("jitter_sd_ms", Math.Round(s.JitterMs, 4));
        w.WriteNumber("p99_ms", Math.Round(s.P99Ms, 4));
        w.WriteEndObject();
    }

    private static void WriteStick(Utf8JsonWriter w, string name, StickDrift s)
    {
        w.WriteStartObject(name);
        w.WriteNumber("mean_x", Math.Round(s.MeanX, 5));
        w.WriteNumber("mean_y", Math.Round(s.MeanY, 5));
        w.WriteNumber("offset_pct", Math.Round(s.OffsetPct, 3));
        w.WriteNumber("max_radius_pct", Math.Round(s.MaxRadiusPct, 3));
        w.WriteNumber("noise_pct", Math.Round(s.NoisePct, 3));
        w.WriteNumber("raw_min_x", s.RawMinX); w.WriteNumber("raw_max_x", s.RawMaxX);
        w.WriteNumber("raw_min_y", s.RawMinY); w.WriteNumber("raw_max_y", s.RawMaxY);
        w.WriteString("verdict", s.Verdict);
        w.WriteNumber("recommended_deadzone_pct", s.RecommendedDeadzonePct);
        w.WriteEndObject();
    }

    /// <summary>One entry per packet interval, polling2 style: delay (ms, 3 decimals), axis, value (-1..1).</summary>
    private static void WriteSample(Utf8JsonWriter w, PollingResult r, int i)
    {
        var cur = r.Capture.States[i + 1];
        var prev = r.Capture.States[i];
        string axis; double value;
        if (cur.LX != prev.LX) { axis = "X"; value = cur.LX / 32767.0; }
        else if (cur.LY != prev.LY) { axis = "Y"; value = cur.LY / 32767.0; }
        else if (cur.RX != prev.RX) { axis = "RX"; value = cur.RX / 32767.0; }
        else if (cur.RY != prev.RY) { axis = "RY"; value = cur.RY / 32767.0; }
        else if (cur.LeftTrigger != prev.LeftTrigger) { axis = "LT"; value = cur.LeftTrigger / 255.0; }
        else if (cur.RightTrigger != prev.RightTrigger) { axis = "RT"; value = cur.RightTrigger / 255.0; }
        else { axis = "BTN"; value = cur.Buttons; }

        w.WriteStartObject();
        w.WriteNumber("delay", Math.Round(r.IntervalsMs[i], 3));
        w.WriteString("axis", axis);
        w.WriteNumber("value", value);
        w.WriteNumber("packet", r.Capture.Packets[i + 1]);
        w.WriteEndObject();
    }
}
