﻿using System;
using System.Diagnostics;
using System.Threading;

using FlyleafLib.MediaFramework.MediaDecoder;

using static FlyleafLib.Utils;
using static FlyleafLib.Logger;

namespace FlyleafLib.MediaPlayer;

unsafe partial class Player
{
    /// <summary>
    /// Fires on buffering started
    /// Warning: Uses Invoke and it comes from playback thread so you can't pause/stop etc. You need to use another thread if you have to.
    /// </summary>
    public event EventHandler BufferingStarted;
    protected virtual void OnBufferingStarted()
    {
        if (onBufferingStarted != onBufferingCompleted) return;
        BufferingStarted?.Invoke(this, new EventArgs()); 
        onBufferingStarted++;

        if (CanDebug) Log.Debug($"OnBufferingStarted");
    }

    /// <summary>
    /// Fires on buffering completed (will fire also on failed buffering completed)
    /// (BufferDration > Config.Player.MinBufferDuration)
    /// Warning: Uses Invoke and it comes from playback thread so you can't pause/stop etc. You need to use another thread if you have to.
    /// </summary>
    public event EventHandler<BufferingCompletedArgs> BufferingCompleted;
    protected virtual void OnBufferingCompleted(string error = null)
    {
        if (onBufferingStarted - 1 != onBufferingCompleted) return;

        if (error != null && LastError == null)
        {
            lastError = error;
            UI(() => LastError = LastError);
        }

        BufferingCompleted?.Invoke(this, new BufferingCompletedArgs(error));
        onBufferingCompleted++;
        if (CanDebug) Log.Debug($"OnBufferingCompleted{(error != null ? $" (Error: {error})" : "")}");
    }

    long    onBufferingStarted;
    long    onBufferingCompleted;

    int     vDistanceMs;
    int     aDistanceMs;
    int     sDistanceMs;
    int     sleepMs;
        
    long    elapsedTicks;
    long    elapsedSec;
    long    startTicks;

    int     allowedLateAudioDrops;
    long    curLatency;
    long    curAudioDeviceDelay;

    Stopwatch sw = new();

    private void ShowOneFrame()
    {
        sFrame = null;
        Subtitles.subsText = "";
        if (Subtitles._SubsText != "")
            UIAdd(() => Subtitles.SubsText = Subtitles.SubsText);

        if (!VideoDecoder.Frames.IsEmpty)
        {
            VideoDecoder.Frames.TryDequeue(out vFrame);
            if (vFrame != null) // might come from video input switch interrupt
                renderer.Present(vFrame);

            if (seeks.IsEmpty)
            {
                if (!VideoDemuxer.IsHLSLive)
                    curTime = vFrame.timestamp;
                UIAdd(() => UpdateCurTime());
                UIAll();
            }

            // Required for buffering on paused
            if (decoder.RequiresResync && !IsPlaying && seeks.IsEmpty)
                decoder.Resync(vFrame.timestamp);

            vFrame = null;
        }

        UIAll();
    }

    private bool MediaBuffer()
    {
        if (CanTrace) Log.Trace("Buffering");

        while (isVideoSwitch && IsPlaying) Thread.Sleep(10);

        Audio.ClearBuffer();

        VideoDemuxer.Start();
        VideoDecoder.Start();

        if (Config.Audio.Enabled)
        {
            curAudioDeviceDelay = Audio.GetDeviceDelay();

            if (AudioDecoder.OnVideoDemuxer)
                AudioDecoder.Start();
            else if (!decoder.RequiresResync)
            {
                AudioDemuxer.Start();
                AudioDecoder.Start();
            }
        }

        if (Config.Subtitles.Enabled)
        {
            lock (lockSubtitles)
            if (SubtitlesDecoder.OnVideoDemuxer)
                SubtitlesDecoder.Start();
            else if (!decoder.RequiresResync)
            {
                SubtitlesDemuxer.Start();
                SubtitlesDecoder.Start();
            }
        }

        VideoDecoder.DisposeFrame(vFrame);
        vFrame = null;
        aFrame = null;
        sFrame = null;
        sFramePrev = null;

        //Subtitles.subsText = "";
        //if (Subtitles._SubsText != "")
        //    UI(() => Subtitles.SubsText = Subtitles.SubsText);

        bool gotAudio       = !Audio.IsOpened || Config.Player.MaxLatency != 0;
        bool gotVideo       = false;
        bool shouldStop     = false;
        bool showOneFrame   = Config.Player.MaxLatency == 0;
        int  audioRetries   = 4;
        int  loops          = 0;

        do
        {
            loops++;

            if (showOneFrame && !VideoDecoder.Frames.IsEmpty)
            {
                ShowOneFrame();
                showOneFrame = false;
            }

            // We allo few ms to show a frame before cancelling
            if ((!showOneFrame || loops > 8) && !seeks.IsEmpty)
                return false;

            if (vFrame == null && !VideoDecoder.Frames.IsEmpty)
            {
                VideoDecoder.Frames.TryDequeue(out vFrame);
                if (!showOneFrame) gotVideo = true;
            }

            if (!gotAudio && aFrame == null && !AudioDecoder.Frames.IsEmpty)
                AudioDecoder.Frames.TryDequeue(out aFrame);

            if (vFrame != null)
            {
                if (decoder.RequiresResync)
                    decoder.Resync(vFrame.timestamp);

                if (!gotAudio && aFrame != null)
                {
                    for (int i=0; i<Math.Min(20, AudioDecoder.Frames.Count); i++)
                    {
                        if (aFrame == null 
                            || aFrame.timestamp - curAudioDeviceDelay > vFrame.timestamp 
                            || vFrame.timestamp > Duration)
                        {
                            gotAudio = true;
                            break;
                        }

                        if (CanInfo) Log.Info($"Drop aFrame {TicksToTime(aFrame.timestamp)}");
                        AudioDecoder.Frames.TryDequeue(out aFrame);
                    }

                    // Avoid infinite loop in case of all audio timestamps wrong
                    if (!gotAudio)
                    {
                        audioRetries--;

                        if (audioRetries < 1)
                        {
                            gotAudio = true;
                            aFrame = null;
                            Log.Warn($"Audio Exhausted 1");
                        }
                    }
                }
            }

            if (!IsPlaying || decoderHasEnded)
                shouldStop = true;
            else
            {
                if (!VideoDecoder.IsRunning && !isVideoSwitch)
                {
                    Log.Warn("Video Exhausted");
                    shouldStop= true;
                }

                if (vFrame != null && !gotAudio && audioRetries > 0 && (!AudioDecoder.IsRunning || AudioDecoder.Demuxer.Status == MediaFramework.Status.QueueFull))
                {
                    if (CanWarn) Log.Warn($"Audio Exhausted 2 | {audioRetries}");

                    audioRetries--;

                    if (audioRetries < 1)
                        gotAudio  = true;
                }
            }

            Thread.Sleep(10);

        } while (!shouldStop && (!gotVideo || !gotAudio));

        if (shouldStop && !(decoderHasEnded && IsPlaying && vFrame != null))
        {
            Log.Info("Stopped");
            return false;
        }

        if (vFrame == null)
        {
            Log.Error("No Frames!");
            return false;
        }

        while(seeks.IsEmpty && VideoDemuxer.BufferedDuration < Config.Player.MinBufferDuration && IsPlaying && VideoDemuxer.IsRunning && VideoDemuxer.Status != MediaFramework.Status.QueueFull) Thread.Sleep(20);

        if (!seeks.IsEmpty)
            return false;

        if (CanInfo) Log.Info($"Started [V: {TicksToTime(vFrame.timestamp)}]" + (aFrame == null ? "" : $" [A: {TicksToTime(aFrame.timestamp)}]"));

        decoder.OpenedPlugin.OnBufferingCompleted();

        return true;
    }
    private void Screamer()
    {
        while (Status == Status.Playing)
        {
            if (seeks.TryPop(out var seekData))
            {
                seeks.Clear();
                requiresBuffering = true;

                decoder.PauseDecoders(); // TBR: Required to avoid gettings packets between Seek and ShowFrame which causes resync issues

                if (decoder.Seek(seekData.ms, seekData.forward, !seekData.accurate) < 0)
                    Log.Warn("Seek failed");
                else if (seekData.accurate)
                    decoder.GetVideoFrame(seekData.ms * (long)10000);
            }
            
            if (requiresBuffering)
            {
                OnBufferingStarted();
                MediaBuffer();
                requiresBuffering = false;
                if (!seeks.IsEmpty)
                    continue;

                if (vFrame == null)
                {
                    if (decoderHasEnded)
                        OnBufferingCompleted();

                    Log.Warn("[MediaBuffer] No video frame");
                    break;
                }
                OnBufferingCompleted();

                allowedLateAudioDrops = 7;
                elapsedSec = 0;
                startTicks = vFrame.timestamp;
                sw.Restart();
            }

            if (Status != Status.Playing)
                break;

            if (vFrame == null)
            {
                if (VideoDecoder.Status == MediaFramework.Status.Ended)
                    break;

                Log.Warn("No video frames");
                requiresBuffering = true;
                continue;
            }

            if (aFrame == null && !isAudioSwitch)
                AudioDecoder.Frames.TryDequeue(out aFrame);

            if (sFrame == null && !isSubsSwitch )
                SubtitlesDecoder.Frames.TryPeek(out sFrame);

            elapsedTicks= sw.ElapsedTicks;

            vDistanceMs = 
                  (int) ((((vFrame.timestamp - startTicks) / speed) - elapsedTicks) / 10000);

            aDistanceMs = aFrame != null 
                ? (int) ((((aFrame.timestamp - startTicks) / speed) - (elapsedTicks - Audio.GetDeviceDelay())) / 10000) 
                : int.MaxValue;

            sDistanceMs = sFrame != null 
                ? (int) ((((sFrame.timestamp - startTicks) / speed) - elapsedTicks) / 10000) 
                : int.MaxValue;

            sleepMs = Math.Min(vDistanceMs, aDistanceMs) - 1;
            if (sleepMs < 0)
                sleepMs = 0;

            if (sleepMs > 2)
            {
                if (vDistanceMs > 2000)
                {
                    Log.Warn($"vDistanceMs = {vDistanceMs} (restarting)");
                    requiresBuffering = true;
                    continue;
                }

                if (Engine.Config.UICurTimePerSecond &&  (
                    (!MainDemuxer.IsHLSLive && curTime / 10000000 != _CurTime / 10000000) ||
                    (MainDemuxer.IsHLSLive && Math.Abs(elapsedTicks - elapsedSec) > 10000000)))
                {
                    elapsedSec  = elapsedTicks;
                    UI(() => UpdateCurTime());
                }

                Thread.Sleep(sleepMs);
            }

            if (Math.Abs(vDistanceMs - sleepMs) <= 2)
            {
                if (CanTrace) Log.Trace($"[V] Presenting {TicksToTime(vFrame.timestamp)}");

                if (decoder.VideoDecoder.Renderer.Present(vFrame))
                    Video.framesDisplayed++;
                else
                    Video.framesDropped++;

                lock (seeks)
                    if (seeks.IsEmpty)
                    {
                        curTime = !MainDemuxer.IsHLSLive ? vFrame.timestamp : VideoDemuxer.CurTime;

                        if (Config.Player.UICurTimePerFrame)
                            UI(() => UpdateCurTime());
                    }

                VideoDecoder.Frames.TryDequeue(out vFrame);
                if (vFrame != null && Config.Player.MaxLatency != 0)
                {
                    curLatency = VideoDemuxer.VideoPackets.Count > 0 ? VideoDemuxer.VideoPackets.LastTimestamp - vFrame.timestamp : 0;
                    double newSpeed = 1;

                    if (curLatency > Config.Player.MaxLatency * 20)
                        { decoder.Flush(); newSpeed = 1; }
                    else if (curLatency > Config.Player.MaxLatency)
                        newSpeed = curLatency > Config.Player.MaxLatency * 10 ? 1.75 : curLatency > Config.Player.MaxLatency * 2 ? 1.5 : 1.1;

                    if (newSpeed != speed)
                    {
                        speed = newSpeed;
                        UI(() => { Raise(nameof(Speed)); });
                        startTicks = vFrame.timestamp;
                        sw.Restart();
                        elapsedSec = 0;
                    }
                }
            }
            else if (vDistanceMs < -2)
            {
                if (vDistanceMs < -10 || VideoDemuxer.BufferedDuration < Config.Player.MinBufferDuration)
                {
                    if (CanInfo)
                        Log.Info($"vDistanceMs = {vDistanceMs} (restarting)");

                    requiresBuffering = true;
                    continue;
                }

                if (CanDebug)
                    Log.Debug($"vDistanceMs = {vDistanceMs}");

                Video.framesDropped++;
                VideoDecoder.DisposeFrame(vFrame);
                VideoDecoder.Frames.TryDequeue(out vFrame);
            }

            if (aFrame != null) // Should use different thread for better accurancy (renderer might delay it on high fps) | also on high offset we will have silence between samples
            {
                if (Math.Abs(aDistanceMs - sleepMs) <= 5)
                {
                    if (CanTrace) Log.Trace($"[A] Presenting {TicksToTime(aFrame.timestamp)}");
                    Audio.AddSamples(aFrame);
                    Audio.framesDisplayed++;
                    AudioDecoder.Frames.TryDequeue(out aFrame);
                }
                else if (aDistanceMs > 1000) // Drops few audio frames in case of wrong timestamps
                {
                    if (allowedLateAudioDrops > 0)
                    {
                        Audio.framesDropped++;
                        allowedLateAudioDrops--;
                        if (CanDebug) Log.Debug($"aDistanceMs 3 = {aDistanceMs}");
                        AudioDecoder.Frames.TryDequeue(out aFrame);
                    }
                }
                else if (aDistanceMs < -5) // Will be transfered back to decoder to drop invalid timestamps
                {
                    if (VideoDemuxer.BufferedDuration < Config.Player.MinBufferDuration)
                    {
                        if (CanInfo)
                            Log.Warn($"Not enough buffer (restarting)");

                        requiresBuffering = true;
                        continue;
                    }

                    if (CanInfo) Log.Info($"aDistanceMs = {aDistanceMs}");

                    if (aDistanceMs < -600)
                    {
                        if (CanTrace) Log.Trace($"All audio frames disposed");
                        Audio.framesDropped += AudioDecoder.Frames.Count;
                        AudioDecoder.DisposeFrames();
                        aFrame = null;
                    }
                    else
                    {
                        int maxdrop = Math.Max(Math.Min(vDistanceMs - sleepMs - 1, 20), 3);
                        for (int i=0; i<maxdrop; i++)
                        {
                            if (CanTrace) Log.Trace($"aDistanceMs 2 = {aDistanceMs}");
                            Audio.framesDropped++;
                            AudioDecoder.Frames.TryDequeue(out aFrame);

                            if (aFrame == null || ((aFrame.timestamp - startTicks) / speed) - (sw.ElapsedTicks - Audio.GetDeviceDelay() + 8 * 1000) > 0)
                                break;

                            aFrame = null;
                        }
                    }
                }
            }
            
            if (sFramePrev != null && ((sFramePrev.timestamp - startTicks + (sFramePrev.duration * (long)10000)) / speed) - sw.ElapsedTicks < 0)
            {
                Subtitles.subsText = "";
                UI(() => Subtitles.SubsText = Subtitles.SubsText);

                sFramePrev = null;
            }

            if (sFrame != null)
            {
                if (Math.Abs(sDistanceMs - sleepMs) < 30 || (sDistanceMs < -30 && sFrame.duration + sDistanceMs > 0))
                {
                    Subtitles.subsText = sFrame.text;
                    UI(() => Subtitles.SubsText = Subtitles.SubsText);
                    
                    sFramePrev = sFrame;
                    sFrame = null;
                    SubtitlesDecoder.Frames.TryDequeue(out var devnull);
                }
                else if (sDistanceMs < -30)
                {
                    if (CanDebug) Log.Debug($"sDistanceMs = {sDistanceMs}");

                    sFrame = null;
                    SubtitlesDecoder.Frames.TryDequeue(out var devnull);
                }
            }
        }

        if (CanInfo) Log.Info($"Finished -> {TicksToTime(CurTime)}");
    }

    private bool AudioBuffer()
    {
        while ((isVideoSwitch || isAudioSwitch) && IsPlaying) Thread.Sleep(10);
        if (!IsPlaying) return false;

        aFrame = null;
        Audio.ClearBuffer();
        decoder.AudioStream.Demuxer.Start();
        AudioDecoder.Start();

        while(AudioDecoder.Frames.IsEmpty && IsPlaying && AudioDecoder.IsRunning) Thread.Sleep(10);
        AudioDecoder.Frames.TryDequeue(out aFrame);
        if (aFrame == null) 
            return false;

        lock (seeks)
            if (seeks.IsEmpty)
            {
                if (MainDemuxer.IsHLSLive)
                    curTime = aFrame.timestamp;
                UI(() => UpdateCurTime());
            }

        while(seeks.IsEmpty && decoder.AudioStream.Demuxer.BufferedDuration < Config.Player.MinBufferDuration && IsPlaying && decoder.AudioStream.Demuxer.IsRunning && decoder.AudioStream.Demuxer.Status != MediaFramework.Status.QueueFull)
            Thread.Sleep(20);

        return IsPlaying && !AudioDecoder.Frames.IsEmpty && seeks.IsEmpty;
    }
    private void ScreamerAudioOnly()
    {
        while (IsPlaying)
        {
            if (seeks.TryPop(out var seekData))
            {
                seeks.Clear();
                requiresBuffering = true;
                
                if (AudioDecoder.OnVideoDemuxer)
                {
                    if (decoder.Seek(seekData.ms, seekData.forward) < 0)
                        Log.Warn("Seek failed 1");
                }
                else
                {
                    if (decoder.SeekAudio(seekData.ms, seekData.forward) < 0)
                        Log.Warn("Seek failed 2");
                }
            }

            if (requiresBuffering)
            {
                OnBufferingStarted();
                AudioBuffer();
                requiresBuffering = false;
                if (!seeks.IsEmpty)
                    continue;
                OnBufferingCompleted();
                if (aFrame == null) { Log.Warn("[MediaBuffer] No audio frame"); break; }
                startTicks = (long) (aFrame.timestamp / Speed);

                Audio.AddSamples(aFrame);
                AudioDecoder.Frames.TryDequeue(out aFrame);
                if (aFrame != null)
                    aFrame.timestamp = (long)(aFrame.timestamp / Speed);
                sw.Restart();
                elapsedSec = 0;
            }

            if (aFrame == null)
            {
                if (AudioDecoder.Status == MediaFramework.Status.Ended)
                    break;

                Log.Warn("No audio frames");
                requiresBuffering = true;
                continue;
            }

            if (Status != Status.Playing) break;

            elapsedTicks = startTicks + sw.ElapsedTicks;
            aDistanceMs  = (int) ((aFrame.timestamp - elapsedTicks) / 10000);

            if (aDistanceMs > 1000 || aDistanceMs < -10)
            {
                requiresBuffering = true;
                continue;
            }

            if (aDistanceMs > 2)
            {
                if (Engine.Config.UICurTimePerSecond && (
                    (!MainDemuxer.IsHLSLive && curTime / 10000000 != _CurTime / 10000000) ||
                    (MainDemuxer.IsHLSLive && Math.Abs(elapsedTicks - elapsedSec) > 10000000)))
                {
                    elapsedSec = elapsedTicks;
                    UI(() => UpdateCurTime());
                }

                Thread.Sleep(aDistanceMs);
            }

            lock (seeks)
                if (!MainDemuxer.IsHLSLive && seeks.IsEmpty)
                {
                    curTime = (long) (aFrame.timestamp * Speed);

                    if (Config.Player.UICurTimePerFrame)
                        UI(() => UpdateCurTime());
                }

            //Log($"{Utils.TicksToTime(aFrame.timestamp)}");
            Audio.AddSamples(aFrame);
            AudioDecoder.Frames.TryDequeue(out aFrame);
            if (aFrame != null)
                aFrame.timestamp = (long)(aFrame.timestamp / Speed);
        }
    }

    private void ScreamerReverse()
    {
        while (Status == Status.Playing)
        {
            if (seeks.TryPop(out var seekData))
            {
                seeks.Clear();
                if (decoder.Seek(seekData.ms, seekData.forward) < 0)
                    Log.Warn("Seek failed");
            }

            if (vFrame == null)
            {
                if (VideoDecoder.Status == MediaFramework.Status.Ended)
                    break;

                OnBufferingStarted();
                if (reversePlaybackResync)
                {
                    decoder.Flush();
                    VideoDemuxer.EnableReversePlayback(CurTime);
                    reversePlaybackResync = false;
                }
                VideoDemuxer.Start();
                VideoDecoder.Start();

                while (VideoDecoder.Frames.IsEmpty && Status == Status.Playing && VideoDecoder.IsRunning) Thread.Sleep(15);
                OnBufferingCompleted();
                VideoDecoder.Frames.TryDequeue(out vFrame);
                if (vFrame == null) { Log.Warn("No video frame"); break; }
                vFrame.timestamp = (long) (vFrame.timestamp / Speed);

                startTicks = vFrame.timestamp;
                sw.Restart();
                elapsedSec = 0;

                if (!MainDemuxer.IsHLSLive && seeks.IsEmpty)
                    curTime = (long) (vFrame.timestamp * Speed);
                UI(() => UpdateCurTime());
            }

            elapsedTicks    = startTicks - sw.ElapsedTicks;
            vDistanceMs     = (int) ((elapsedTicks - vFrame.timestamp) / 10000);
            sleepMs         = vDistanceMs - 1;

            if (sleepMs < 0) sleepMs = 0;

            if (Math.Abs(vDistanceMs - sleepMs) > 5)
            {
                //Log($"vDistanceMs |-> {vDistanceMs}");
                VideoDecoder.DisposeFrame(vFrame);
                vFrame = null;
                Thread.Sleep(5);
                continue; // rebuffer
            }

            if (sleepMs > 2)
            {
                if (sleepMs > 1000)
                {
                    //Log($"sleepMs -> {sleepMs} , vDistanceMs |-> {vDistanceMs}");
                    VideoDecoder.DisposeFrame(vFrame);
                    vFrame = null;
                    Thread.Sleep(5);
                    continue; // rebuffer
                }

                // Every seconds informs the application with CurTime / Bitrates (invokes UI thread to ensure the updates will actually happen)
                if (Engine.Config.UICurTimePerSecond && (
                    (!MainDemuxer.IsHLSLive && curTime / 10000000 != _CurTime / 10000000) || 
                    (MainDemuxer.IsHLSLive && Math.Abs(elapsedTicks - elapsedSec) > 10000000)))
                {
                    elapsedSec  = elapsedTicks;
                    UI(() => UpdateCurTime());
                }

                Thread.Sleep(sleepMs);
            }

            decoder.VideoDecoder.Renderer.Present(vFrame);
            if (!MainDemuxer.IsHLSLive && seeks.IsEmpty)
            {
                curTime = (long) (vFrame.timestamp * Speed);

                if (Config.Player.UICurTimePerFrame)
                    UI(() => UpdateCurTime());
            }
                
            VideoDecoder.Frames.TryDequeue(out vFrame);
            if (vFrame != null)
                vFrame.timestamp = (long) (vFrame.timestamp / Speed);
        }
    }
}

public class BufferingCompletedArgs : EventArgs
{
    public string   Error       { get; }
    public bool     Success     { get; }
        
    public BufferingCompletedArgs(string error)
    {
        Error   = error;
        Success = Error == null;
    }
}
