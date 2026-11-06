using Microsoft.Win32.SafeHandles;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WinRTOutput;

internal sealed unsafe class StringPointer : SafeHandleZeroOrMinusOneIsInvalid
{
	public static nint DangerousAllocateStringPointer(in string str)
	{
		var result = (nint)NativeMemory.AlignedAlloc((nuint)(sizeof(char) * (str.Length + 1)), sizeof(char));
		fixed (char* source = str)
		{
			NativeMemory.Copy(source, (char*)result, (nuint)str.Length * sizeof(char));
		}
		((char*)result)[str.Length] = '\0';
		return result;
	}
	public static void DangerousFreeStringPointer(ref nint ptr) => NativeMemory.AlignedFree((void*)ptr);
	public StringPointer(in string str) : base(true) => handle = DangerousAllocateStringPointer(in str);
	protected override bool ReleaseHandle()
	{
		DangerousFreeStringPointer(ref handle);
		return true;
	}
}

internal unsafe class Logging(nint logger)
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
	public readonly struct Logger
	{
		public readonly delegate* unmanaged[Stdcall]<Logger*, nint, void> Log;
		public readonly delegate* unmanaged[Stdcall]<Logger*, nint, void> LogInfo;
		public readonly delegate* unmanaged[Stdcall]<Logger*, nint, void> Warn;
		public readonly delegate* unmanaged[Stdcall]<Logger*, nint, void> Err;
		public readonly delegate* unmanaged[Stdcall]<Logger*, nint, void> VerboseLog;
	}
	public void Log(string message)
	{
		using var messagePtr = new StringPointer(message);
		((Logger*)logger)->Log((Logger*)logger, messagePtr.DangerousGetHandle());
	}
	public void LogInfo(string message)
	{
		using var messagePtr = new StringPointer(message);
		((Logger*)logger)->LogInfo((Logger*)logger, messagePtr.DangerousGetHandle());
	}
	public void Warn(string message)
	{
		using var messagePtr = new StringPointer(message);
		((Logger*)logger)->Warn((Logger*)logger, messagePtr.DangerousGetHandle());
	}
	public void Err(string message)
	{
		using var messagePtr = new StringPointer(message);
		((Logger*)logger)->Err((Logger*)logger, messagePtr.DangerousGetHandle());
	}
	public void VerboseLog(string message)
	{
		using var messagePtr = new StringPointer(message);
		((Logger*)logger)->VerboseLog((Logger*)logger, messagePtr.DangerousGetHandle());
	}
}

class Output
{
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
	public readonly struct OutputInfo
	{
		public readonly int flags;
		public readonly int width, height;
		public readonly int rate, scale;
		public readonly int frameCount;
		public readonly int audioRate;
		public readonly int audioChannelCount;
		public readonly int audioSamplingCount;
		public readonly nint destination;
		public readonly delegate* unmanaged[Stdcall]<int, uint, void*> GetVideoFramePixels;
		public readonly delegate* unmanaged[Stdcall]<int, int, out int, uint, void*> GetAudioSample;
		public readonly delegate* unmanaged[Stdcall]<bool> IsAborted;
		public readonly delegate* unmanaged[Stdcall]<int, int, void> SetEstimatedTimeOfCompletion;
		public readonly delegate* unmanaged[Stdcall]<int, int, void> SetBufferCacheSize;
	}

	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
	public struct OutputPluginTable
	{
		public int flags;
		public nint pluginName;
		public nint fileFilters;
		public nint pluginDescription;
		public delegate* unmanaged[Stdcall]<nint, bool> Output;
		public delegate* unmanaged[Stdcall]<nint, nint, bool> OpenOutputConfiguration;
		public delegate* unmanaged[Stdcall]<nint, bool> LoadProjectConfiguration;
		public delegate* unmanaged[Stdcall]<nint, bool> SaveProjectConfiguration;
	}

	private protected static Logging? Logger { get; set; }
	private protected static OutputPluginTable pluginTable;

	[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
	private protected static unsafe bool OutputFile(nint ptr)
	{
		var outputInfo = Unsafe.BitCast<nint, OutputInfo>(ptr);

		Logger?.LogInfo("Configuring video input...");
		var inputVideoEncodingProperties = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Yuy2, (uint)outputInfo.width, (uint)outputInfo.height);
		var inputVideoStreamDescriptor = new VideoStreamDescriptor(inputVideoEncodingProperties);
		inputVideoStreamDescriptor.EncodingProperties.FrameRate.Numerator = (uint)outputInfo.rate;
		inputVideoStreamDescriptor.EncodingProperties.FrameRate.Denominator = (uint)outputInfo.scale;

		Logger?.LogInfo("Configuring audio input...");
		var inputAudioEncodingProperties = AudioEncodingProperties.CreatePcm((uint)outputInfo.audioRate, (uint)outputInfo.audioChannelCount, 16);
		var inputAudioStreamDescriptor = new AudioStreamDescriptor(inputAudioEncodingProperties);

		Logger?.LogInfo("Preparing media stream...");
		var mediaStreamSource = new MediaStreamSource(inputVideoStreamDescriptor, inputAudioStreamDescriptor);
		mediaStreamSource.Starting += (sender, args) =>
		{
			Logger?.LogInfo("MSS starting...");
			args.Request.SetActualStartPosition(TimeSpan.Zero);
		};
		var currentVideoFrameIndex = 0;
		var currentAudioSampleIndex = 0;
		mediaStreamSource.SampleRequested += (sender, args) =>
		{
			var request = args.Request;

			switch (request.StreamDescriptor)
			{
				case VideoStreamDescriptor:
					Logger?.LogInfo("Video sample requested.");
					if (currentVideoFrameIndex >= outputInfo.frameCount)
					{
						request.Sample = null;
						return;
					}
					outputInfo.SetEstimatedTimeOfCompletion(currentVideoFrameIndex, outputInfo.frameCount);
					var videoFramePixels = outputInfo.GetVideoFramePixels(outputInfo.frameCount, (uint)currentVideoFrameIndex);
					var videoFrameBuffer = new Windows.Storage.Streams.Buffer((uint)(outputInfo.width * outputInfo.height * 2));
					new ReadOnlySpan<byte>(videoFramePixels, (int)videoFrameBuffer.Length)
					.ToArray()
					.AsSpan()
					.CopyTo(videoFrameBuffer);
					request.Sample = MediaStreamSample.CreateFromBuffer
					(
						videoFrameBuffer,
						TimeSpan.FromSeconds((double)outputInfo.scale / outputInfo.rate * currentVideoFrameIndex)
					);
					++currentVideoFrameIndex;
					return;


				case AudioStreamDescriptor:
					Logger?.LogInfo("Audio sample requested.");
					var audioSampleData = outputInfo.GetAudioSample
					(
						outputInfo.audioSamplingCount,
						outputInfo.audioChannelCount,
						out var actualSampleCount,
						(uint)currentAudioSampleIndex
					);
					if (actualSampleCount == 0)
					{
						request.Sample = null;
						return;
					}

					var audioSampleBuffer = new Windows.Storage.Streams.Buffer((uint)(actualSampleCount * outputInfo.audioChannelCount * 2));

					new ReadOnlySpan<byte>(audioSampleData, (int)audioSampleBuffer.Length)
					.ToArray()
					.AsSpan()
					.CopyTo(audioSampleBuffer);
					request.Sample = MediaStreamSample.CreateFromBuffer
					(
						audioSampleBuffer,
						TimeSpan.FromSeconds((double)outputInfo.audioSamplingCount / outputInfo.audioRate)
					);
					currentAudioSampleIndex += outputInfo.audioRate;
					return;

				default:
					return;
			}
		};

		Logger?.LogInfo("Output format is MP4.");
		var mediaEncodingProfile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);

		Logger?.LogInfo("Preparing media transcoder...");
		var mediaTranscoder = new MediaTranscoder
		{
			HardwareAccelerationEnabled = true
		};

		Logger?.LogInfo("Preparing destination file...");
		using var outputDestination = Task.Run(async () => await FileRandomAccessStream.OpenAsync
			(
			Marshal.PtrToStringUni(outputInfo.destination),
			FileAccessMode.ReadWrite,
			StorageOpenOptions.AllowReadersAndWriters,
			FileOpenDisposition.CreateAlways
			))
			.GetAwaiter().GetResult();

		Logger?.LogInfo("Preparing transcoder...");
		var result = Task.Run(async () => await mediaTranscoder.PrepareMediaStreamSourceTranscodeAsync
			(
			mediaStreamSource,
			outputDestination,
			mediaEncodingProfile
			))
			.GetAwaiter().GetResult();
		if (result.CanTranscode)
		{
			Logger?.LogInfo("Working...");
			Task.Run(async () => await result.TranscodeAsync()).GetAwaiter().GetResult();
		}
		else
		{
			switch (result.FailureReason)
			{
				case TranscodeFailureReason.CodecNotFound:
					Logger?.Err("Codec not found");
					break;
				case TranscodeFailureReason.InvalidProfile:
					Logger?.Err("Invalid profile");
					break;
				case TranscodeFailureReason.Unknown:
					Logger?.Err("Unknown error");
					break;
			}
			return false;
		}

		return true;
	}
	[UnmanagedCallersOnly(EntryPoint = "InitializePlugin", CallConvs = [typeof(CallConvStdcall)])]
	public static unsafe bool InitializePlugin(uint _)
	{
		pluginTable = Unsafe.BitCast<nint, OutputPluginTable>((nint)NativeMemory.Alloc((nuint)sizeof(OutputPluginTable)));
		pluginTable.flags = (1 << 0) | (1 << 1);
		pluginTable.pluginName = StringPointer.DangerousAllocateStringPointer("WinRTOutput");
		pluginTable.fileFilters = StringPointer.DangerousAllocateStringPointer("MP4 (*.mp4)\0*.mp4\0");
		pluginTable.pluginDescription = StringPointer.DangerousAllocateStringPointer("WinRTOutput");
		pluginTable.Output = &OutputFile;
		return true;
	}
	[UnmanagedCallersOnly(EntryPoint = "UninitializePlugin", CallConvs = [typeof(CallConvStdcall)])]
	public static unsafe void UninitializePlugin()
	{
		StringPointer.DangerousFreeStringPointer(ref pluginTable.pluginName);
		StringPointer.DangerousFreeStringPointer(ref pluginTable.fileFilters);
		StringPointer.DangerousFreeStringPointer(ref pluginTable.pluginDescription);
		NativeMemory.Free((void*)Unsafe.BitCast<OutputPluginTable, nint>(pluginTable));
	}
	[UnmanagedCallersOnly(EntryPoint = "InitializeLogger", CallConvs = [typeof(CallConvStdcall)])]
	public static void InitializeLogger(nint ptr) => Logger = new Logging(ptr);

	[UnmanagedCallersOnly(EntryPoint = "GetOutputPluginTable", CallConvs = [typeof(CallConvStdcall)])]
	public static nint GetOutputPluginTable() => Unsafe.BitCast<OutputPluginTable, nint>(pluginTable);
}