﻿using SharpTox.Av;
using System;
using System.Linq;
using Toxy.ViewModels;
using Toxy.Extensions;
using SharpTox.Core;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toxy.Tools;

namespace Toxy.Managers
{
    public class CallManager : IToxManager
    {
        private volatile CallInfo _callInfo;
        private Tox _tox;
        private ToxAv _toxAv;

        private void Tox_OnFriendConnectionStatusChanged(object sender, SharpTox.Core.ToxEventArgs.FriendConnectionStatusEventArgs e)
        {
            if (e.Status != ToxConnectionStatus.None)
                return;

            if (_callInfo != null && _callInfo.FriendNumber == e.FriendNumber)
            {
                ToxAv_OnCallStateChanged(null, new ToxAvEventArgs.CallStateEventArgs(e.FriendNumber, ToxAvFriendCallState.Finished));
            }
        }

        private void ToxAv_OnBitrateSuggestion(object sender, ToxAvEventArgs.BitrateStatusEventArgs e)
        {
            Debugging.Write(string.Format("Applying ToxAV suggestion: {1} for audio, {2} for video", e.AudioBitrate, e.VideoBitrate));

            var error = ToxAvErrorSetBitrate.Ok;
            if (!_toxAv.SetAudioBitrate(e.FriendNumber, e.AudioBitrate, out error))
                Debugging.Write(string.Format("Could not set audio bitrate to {0}, error: {1}", e.AudioBitrate, error));

            if (!_toxAv.SetVideoBitrate(e.FriendNumber, e.VideoBitrate, out error))
                Debugging.Write(string.Format("Could not set video bitrate to {0}, error: {1}", e.AudioBitrate, error));
        }

        public void ToggleVideo(bool enableVideo)
        {
            if (_callInfo == null)
                return;

            _toxAv.SetVideoBitrate(_callInfo.FriendNumber, enableVideo ? 3000 : 0);

            if (!enableVideo && _callInfo.VideoEngine != null)
            {
                _callInfo.VideoEngine.Dispose();
                _callInfo.VideoEngine = null;
            }
            else if (enableVideo)
            {
                if (_callInfo.VideoEngine != null)
                {
                    _callInfo.VideoEngine.Dispose();
                    _callInfo.VideoEngine = null;
                }

                _callInfo.VideoEngine = new VideoEngine();
                _callInfo.VideoEngine.OnFrameAvailable += VideoEngine_OnFrameAvailable;
                _callInfo.VideoEngine.StartRecording();
            }
        }

        private void ToxAv_OnCallRequestReceived(object sender, ToxAvEventArgs.CallRequestEventArgs e)
        {
            if (_callInfo != null)
            {
                //TODO: notify the user there's yet another call incoming
                _toxAv.SendControl(e.FriendNumber, ToxAvCallControl.Cancel);
                return;
            }

            MainWindow.Instance.UInvoke(() =>
            {
                var friend = MainWindow.Instance.ViewModel.CurrentFriendListView.FindFriend(e.FriendNumber);
                if (friend == null)
                {
                    Debugging.Write("Received a call request from a friend we don't know about!");
                    return;
                }

                friend.CallState = CallState.Calling;

                if (e.VideoEnabled)
                    friend.CallState |= CallState.SendingVideo;
            });
        }

        private void ToxAv_OnCallStateChanged(object sender, ToxAvEventArgs.CallStateEventArgs e)
        {
            var state = CallState.InProgress;

            if ((e.State & ToxAvFriendCallState.Finished) != 0 || (e.State & ToxAvFriendCallState.Error) != 0)
            {
                if (_callInfo != null)
                {
                    _callInfo.Dispose();
                    _callInfo = null;
                }

                state = CallState.None;
            }
            else if ((e.State & ToxAvFriendCallState.ReceivingAudio) != 0 ||
                (e.State & ToxAvFriendCallState.ReceivingVideo) != 0 ||
                (e.State & ToxAvFriendCallState.SendingAudio) != 0 ||
                (e.State & ToxAvFriendCallState.SendingVideo) != 0)
            {
                //start sending whatever from here
                if (_callInfo.AudioEngine != null)
                {
                    if (!_callInfo.AudioEngine.IsRecording)
                        _callInfo.AudioEngine.StartRecording();
                }

                if (_callInfo.VideoEngine != null)
                {
                    if (!_callInfo.VideoEngine.IsRecording)
                        _callInfo.VideoEngine.StartRecording();
                }

                if (e.State.HasFlag(ToxAvFriendCallState.SendingVideo))
                    state |= CallState.SendingVideo;

                if (_callInfo.VideoEngine != null)
                    state |= CallState.ReceivingVideo;
            }

            MainWindow.Instance.UInvoke(() =>
            {
                var friend = MainWindow.Instance.ViewModel.CurrentFriendListView.FindFriend(e.FriendNumber);
                if (friend == null)
                {
                    Debugging.Write("Received a call state change from a friend we don't know about!");
                    return;
                }

                friend.CallState = state;
            });
        }

        private void ToxAv_OnVideoFrameReceived(object sender, ToxAvEventArgs.VideoFrameEventArgs e)
        {
            var source = VideoUtils.ToxAvFrameToBitmap(e.Frame);

            MainWindow.Instance.UInvoke(() =>
            {
                var friend = MainWindow.Instance.ViewModel.CurrentFriendListView.FindFriend(e.FriendNumber);
                if (friend == null)
                    return;

                (friend.ConversationView as ConversationViewModel).CurrentFrame = source;
            });
        }

        private void VideoEngine_OnFrameAvailable(Bitmap bmp)
        {
            if (_callInfo == null)
                return;

            var frame = VideoUtils.BitmapToToxAvFrame(bmp);
            bmp.Dispose();

            //yes, check again
            if (_callInfo == null)
                return;

            var error = ToxAvErrorSendFrame.Ok;
            if (!_toxAv.SendVideoFrame(_callInfo.FriendNumber, frame, out error))
                Debugging.Write("Could not send video frame: " + error);
        }

        private void AudioEngine_OnMicDataAvailable(short[] data, int sampleRate, int channels)
        {
            if (_callInfo == null)
                return;

            var error = ToxAvErrorSendFrame.Ok;
            if (!_toxAv.SendAudioFrame(_callInfo.FriendNumber, new ToxAvAudioFrame(data, sampleRate, channels), out error))
            {
                Debugging.Write("Failed to send audio frame: " + error);
            }
        }

        private void ToxAv_OnAudioFrameReceived(object sender, SharpTox.Av.ToxAvEventArgs.AudioFrameEventArgs e)
        {
            if (_callInfo != null && _callInfo.AudioEngine != null)
            {
                //in case the friend suddenly changed audio config, account for it here
                if (e.Frame.Channels != _callInfo.AudioEngine.PlaybackFormat.Channels ||
                    e.Frame.SamplingRate != _callInfo.AudioEngine.PlaybackFormat.SampleRate)
                    _callInfo.AudioEngine.SetPlaybackSettings(e.Frame.SamplingRate, e.Frame.Channels);

                //send the frame to the audio engine
                _callInfo.AudioEngine.ProcessAudioFrame(e.Frame);
            }
        }

        public bool Answer(int friendNumber, bool enableVideo)
        {
            if (_callInfo != null)
            {
                Debugging.Write("Tried to answer a call but there is already one in progress");
                return false;
            }

            var error = ToxAvErrorAnswer.Ok;
            if (!_toxAv.Answer(friendNumber, 48, enableVideo ? 3000 : 0, out error))
            {
                Debugging.Write("Could not answer call for friend: " + error);
                return false;
            }

            _callInfo = new CallInfo(friendNumber);
            _callInfo.AudioEngine = new AudioEngine();
            _callInfo.AudioEngine.OnMicDataAvailable += AudioEngine_OnMicDataAvailable;
            _callInfo.AudioEngine.StartRecording();

            if (enableVideo)
            {
                _callInfo.VideoEngine = new VideoEngine();
                _callInfo.VideoEngine.OnFrameAvailable += VideoEngine_OnFrameAvailable;
                _callInfo.VideoEngine.StartRecording();
            }

            return true;
        }

        public bool Hangup(int friendNumber)
        {
            var error = ToxAvErrorCallControl.Ok;
            if (!_toxAv.SendControl(friendNumber, ToxAvCallControl.Cancel, out error))
            {
                Debugging.Write("Could not answer call for friend: " + error);
                return false;
            }

            _callInfo.Dispose();
            _callInfo = null;
            return true;
        }

        public bool SendRequest(int friendNumber, bool enableVideo)
        {
            if (_callInfo != null)
            {
                Debugging.Write("Tried to send a call request but there is already one in progress");
                return false;
            }

            var error = ToxAvErrorCall.Ok;
            if (!_toxAv.Call(friendNumber, 48, enableVideo ? 3000 : 0, out error))
            {
                Debugging.Write("Could not send call request to friend: " + error);
                return false;
            } 
            
            _callInfo = new CallInfo(friendNumber);
            _callInfo.AudioEngine = new AudioEngine();
            _callInfo.AudioEngine.OnMicDataAvailable += AudioEngine_OnMicDataAvailable;

            if (enableVideo)
            {
                _callInfo.VideoEngine = new VideoEngine();
                _callInfo.VideoEngine.OnFrameAvailable += VideoEngine_OnFrameAvailable;
            }

            return true;
        }

        public void Kill()
        {
            if (_callInfo == null)
                return;

            if (_callInfo.AudioEngine != null)
                _callInfo.AudioEngine.Dispose();

            if (_callInfo.VideoEngine != null)
                _callInfo.VideoEngine.Dispose();
        }

        public void SwitchProfile(Tox tox, ToxAv toxAv)
        {
            _tox = tox;
            _toxAv = toxAv;

            _toxAv.OnAudioFrameReceived += ToxAv_OnAudioFrameReceived;
            _toxAv.OnVideoFrameReceived += ToxAv_OnVideoFrameReceived;
            _toxAv.OnCallStateChanged += ToxAv_OnCallStateChanged;
            _toxAv.OnCallRequestReceived += ToxAv_OnCallRequestReceived;
            _toxAv.OnBitrateSuggestion += ToxAv_OnBitrateSuggestion;
            _tox.OnFriendConnectionStatusChanged += Tox_OnFriendConnectionStatusChanged;
        }

        private class CallInfo : IDisposable
        {
            public readonly int FriendNumber;
            public AudioEngine AudioEngine { get; set; }
            public VideoEngine VideoEngine { get; set; }

            public CallInfo(int friendNumber)
            {
                FriendNumber = friendNumber;
            }
        
            public void Dispose()
            {
                if (AudioEngine != null)
                    AudioEngine.Dispose();

                if (VideoEngine != null)
                    VideoEngine.Dispose();
            }
        }
    }

    public enum CallState
    {
        None = 1 << 0,
        Ringing = 1 << 1,
        Calling = 1 << 2,
        InProgress = 1 << 3,
        SendingVideo = 1 << 4,
        ReceivingVideo = 1 << 5
    }
}
