﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Timers;
using Accord.Math.Optimization;
using Resgrid.Audio.Core.Events;
using Resgrid.Audio.Core.Model;

namespace Resgrid.Audio.Core
{
	public interface IAudioProcessor
	{
		void Init(Config config);
		void Start();
	}

	public class AudioProcessor : IAudioProcessor
	{
		private static Object _lock = new Object();
		private static bool _timerProcessing = false;

		private const int BUFFER_SIZE = 26400000; // 5 Minute Buffer, 1 second = 88,000 array elements
		private const int ONE_SEC = 88000;

		private bool _initialized = false;
		private Queue<byte> _buffer;
		private Settings _settings;

		private Config _config;
		private Dictionary<Guid, Watcher> _startedWatchers;

		private readonly IAudioRecorder _audioRecorder;
		private readonly IAudioEvaluator _audioEvaluator;
		private readonly SampleAggregator _sampleAggregator;
		private Timer _timer;

		public event EventHandler<TriggerProcessedEventArgs> TriggerProcessingStarted;
		public event EventHandler<TriggerProcessedEventArgs> TriggerProcessingFinished;

		public AudioProcessor(IAudioRecorder audioRecorder, IAudioEvaluator audioEvaluator)
		{
			_audioRecorder = audioRecorder;
			_audioEvaluator = audioEvaluator;

			_buffer = new Queue<byte>(BUFFER_SIZE);
			_sampleAggregator = new SampleAggregator();
			_startedWatchers = new Dictionary<Guid, Watcher>();

			_audioRecorder.SetSampleAggregator(_sampleAggregator);

			_timer = new Timer(500);
			_timer.Elapsed += _timer_Elapsed;
		}

		private void _timer_Elapsed(object sender, ElapsedEventArgs e)
		{
			if (!_timerProcessing)
			{
				_timerProcessing = true;

				// This timer is the first pass, _sampleAggregator_DataAvailable looking for silence is a better long term pattern.
				if (_startedWatchers != null && _startedWatchers.Any())
				{
					List<Guid> watchersToRemove = new List<Guid>();

					lock (_lock)
					{
						foreach (var startedWatcher in _startedWatchers)
						{
							var diffInSeconds = (DateTime.UtcNow - startedWatcher.Value.LastCheckedTimestamp).TotalSeconds;

							lock (_lock)
							{
								startedWatcher.Value.AddAudio(_buffer.Take((int) diffInSeconds * ONE_SEC).ToArray());
							}

							startedWatcher.Value.LastCheckedTimestamp = DateTime.UtcNow;

							if ((DateTime.UtcNow - startedWatcher.Value.TriggerFiredTimestamp).TotalSeconds >= _config.AudioLength)
							{
								watchersToRemove.Add(startedWatcher.Value.Id);
							}
						}

						if (watchersToRemove != null && watchersToRemove.Any())
						{
							foreach (var id in watchersToRemove)
							{
								var watcher = _startedWatchers[id];
								TriggerProcessingFinished?.Invoke(this,
									new TriggerProcessedEventArgs(watcher, watcher.GetTrigger(), DateTime.UtcNow));

								_startedWatchers.Remove(id);
							}

							_audioEvaluator.ClearTones();
							watchersToRemove.Clear();
						}
					}
				}

				_timerProcessing = false;
			}
		}

		public void Init(Config config)
		{
			_config = config;

			if (_sampleAggregator != null && !_initialized)
			{
				_sampleAggregator.WaveformCalculated += _sampleAggregator_WaveformCalculated;
				_sampleAggregator.DataAvailable += _sampleAggregator_DataAvailable;
				_audioEvaluator.WatcherTriggered += _audioEvaluator_WatcherTriggered;

				_audioEvaluator.Init(_config);

				_timer.Interval = config.AudioLength;
				_timer.Enabled = true;

				_initialized = true;
			}
		}

		private void _audioEvaluator_WatcherTriggered(object sender, WatcherEventArgs e)
		{
			lock (_lock)
			{
				AddTriggeredWatcher(e.Watcher, e.Triggers.FirstOrDefault(), _buffer.Take(1320000).ToArray(), e.Timestamp);
			}
		}

		public void Start()
		{
			Init(_config);
			_audioRecorder.BeginMonitoring(_config.InputDevice);
		}

		private void _sampleAggregator_DataAvailable(object sender, DataAvailableArgs e)
		{
			if (e?.Buffer != null)
			{
				//foreach (var buffer in e.Buffer)
				//{
				//if (!IsSilence(buffer, _config.Threshold))
				//	_buffer.Enqueue(buffer);
				//}

				for (int index = 0; index < e.BytesRecorded; index += 2)
				{
					short sample = (short)((e.Buffer[index + 1] << 8) | e.Buffer[index + 0]);
					float sample32 = sample / 32768f;

					if (!IsSilence(sample32, _config.Threshold))
					{
						lock (_lock)
						{
							_buffer.Enqueue(e.Buffer[index + 0]);
							_buffer.Enqueue(e.Buffer[index + 1]);
						}
					}
				}
			}
		}

		private void _sampleAggregator_WaveformCalculated(object sender, WaveformEventArgs e)
		{

		}

		private void AddTriggeredWatcher(Watcher watcher, Trigger trigger, byte[] audio, DateTime timeStamp)
		{
			if (watcher != null)
			{
				if (_startedWatchers.Count > 0 && !_config.Multiple)
				{
					_startedWatchers.First().Value.AddAdditionalWatcher(watcher);
				}
				else if (!_startedWatchers.ContainsKey(watcher.Id))
				{
					watcher.TriggerFiredTimestamp = timeStamp;
					watcher.LastCheckedTimestamp = timeStamp;
					watcher.SetFiredTrigger(trigger);
					watcher.AddAudio(audio);
					_startedWatchers.Add(watcher.Id, watcher);
				}
			}
		}

		private bool IsSilence(float amplitude, sbyte threshold)
		{
			double dB = 20 * Math.Log10(Math.Abs(amplitude));
			//var isSilence = dB < threshold;
			var isSilence = dB > threshold;

			return isSilence;
		}
	}
}
