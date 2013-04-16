﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VidCoder
{
	using System.Threading;
	using HandBrake.Interop;
	using HandBrake.Interop.EventArgs;
	using HandBrake.Interop.SourceData;
	using Model;
	using Services;

	public class LocalEncodeProxy : IEncodeProxy
	{
		private ILogger logger;

		// Instance and lock only used when doing in-process encode (for debugging)
		private HandBrakeInstance instance;
		private object encoderLock = new object();

		private ManualResetEventSlim encodeStartEvent;
		private ManualResetEventSlim encodeEndEvent;

		private bool encoding;

		public event EventHandler EncodeStarted;
		public event EventHandler<EncodeProgressEventArgs> EncodeProgress;
		public event EventHandler<EncodeCompletedEventArgs> EncodeCompleted;
		public bool IsEncodeStarted { get; private set; }
		public void StartEncode(VCJob job, ILogger logger, bool preview, int previewNumber, int previewSeconds, double overallSelectedLengthSeconds)
		{
			this.logger = logger;
			this.logger.Log("Starting encode in-process");

			this.encoding = true;

			this.encodeStartEvent = new ManualResetEventSlim(false);
			this.encodeEndEvent = new ManualResetEventSlim(false);

			this.instance = new HandBrakeInstance();
			this.instance.Initialize(Config.LogVerbosity);

			this.instance.ScanCompleted += (o, e) =>
			{
				try
				{
					Title encodeTitle = this.instance.Titles.FirstOrDefault(title => title.TitleNumber == job.Title);

					if (encodeTitle != null)
					{
						lock (this.encoderLock)
						{
							this.instance.StartEncode(job.HbJob, preview, previewNumber, previewSeconds, overallSelectedLengthSeconds);
							this.IsEncodeStarted = true;
							if (this.EncodeStarted != null)
							{
								this.EncodeStarted(this, new EventArgs());
							}

							this.encodeStartEvent.Set();
						}
					}
					else
					{
						if (this.EncodeCompleted != null)
						{
							this.EncodeCompleted(this, new EncodeCompletedEventArgs { Error = true });
						}

						this.encodeStartEvent.Set();
						this.encodeEndEvent.Set();
					}
				}
				catch (Exception exception)
				{
					this.logger.LogError("Encoding failed. Please report this error so it can be fixed in the future:" + Environment.NewLine + exception);
				}
			};

			this.instance.EncodeProgress += (o, e) =>
			{
				// Dispatch to avoid deadlocks on callbacks
				DispatchService.BeginInvoke(() =>
				{
					lock (this.encoderLock)
					{
						if (this.encoding && this.EncodeProgress != null)
						{
							this.EncodeProgress(this, e);
						}
					}
				});
			};

			this.instance.EncodeCompleted += (o, e) =>
			{
				if (this.encoding)
				{
					if (this.EncodeCompleted != null)
					{
						this.EncodeCompleted(this, e);
					}

					this.encoding = false;
				}

				this.encodeEndEvent.Set();
				this.instance.Dispose();
			};

			this.instance.StartScan(job.SourcePath, Config.PreviewCount, job.Title);

			this.encoding = true;
		}

		public void PauseEncode()
		{
			this.encodeStartEvent.Wait();

			lock (this.encoderLock)
			{
				this.instance.PauseEncode();
			}
		}

		public void ResumeEncode()
		{
			this.instance.ResumeEncode();
		}

		public void StopEncode()
		{
			this.encodeStartEvent.Wait();

			lock (this.encoderLock)
			{
				this.instance.StopEncode();
			}
		}

		public void StopAndWait()
		{
			this.encodeStartEvent.Wait();

			lock (this.encoderLock)
			{
				if (this.encoding)
				{
					this.instance.StopEncode();
				}
			}

			this.encodeEndEvent.Wait();
		}
	}
}