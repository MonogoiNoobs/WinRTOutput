using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WinRTOutput;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
public readonly unsafe struct Logger
{
	public readonly delegate* unmanaged[Stdcall]<Logger*, nint, void> Log;
	public readonly delegate* unmanaged[Stdcall]<Logger*, nint, void> LogInfo;
	public readonly delegate* unmanaged[Stdcall]<Logger*, nint, void> Warn;
	public readonly delegate* unmanaged[Stdcall]<Logger*, nint, void> Err;
	public readonly delegate* unmanaged[Stdcall]<Logger*, nint, void> LogVerbosely;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
public readonly unsafe struct OutputInfo
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
public unsafe struct OutputPluginTable
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

class Output
{
	private unsafe static Logger* Logger { get; set; }
	private unsafe static OutputPluginTable* PluginTable { get; set; }

	[UnmanagedCallersOnly(CallConvs = new Type[] { typeof(CallConvStdcall) })]
	public unsafe static bool OutputFile(nint ptr)
	{
		var outputInfo = (OutputInfo*)ptr;

		Logger->LogInfo(Logger, Marshal.StringToHGlobalAuto("Configuring video input..."));
		var inputVideoEncodingProperties = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Yuy2, (uint)outputInfo->width, (uint)outputInfo->height);
		var inputVideoStreamDescriptor = new VideoStreamDescriptor(inputVideoEncodingProperties);
		inputVideoStreamDescriptor.EncodingProperties.FrameRate.Numerator = (uint)outputInfo->rate;
		inputVideoStreamDescriptor.EncodingProperties.FrameRate.Denominator = (uint)outputInfo->scale;

		Logger->LogInfo(Logger, Marshal.StringToHGlobalAuto("Configuring audio input..."));
		var inputAudioEncodingProperties = AudioEncodingProperties.CreatePcm((uint)outputInfo->audioRate, (uint)outputInfo->audioChannelCount, 16);
		var inputAudioStreamDescriptor = new AudioStreamDescriptor(inputAudioEncodingProperties);

		Logger->LogInfo(Logger, Marshal.StringToHGlobalAuto("Preparing media stream..."));
		var mediaStreamSource = new MediaStreamSource(inputVideoStreamDescriptor, inputAudioStreamDescriptor);
		var currentVideoFrameIndex = 0;
		var currentAudioSampleIndex = 0;
		mediaStreamSource.SampleRequested += (sender, args) =>
		{
			var request = args.Request;

			if (request.StreamDescriptor is VideoStreamDescriptor)
			{
				if (currentVideoFrameIndex >= outputInfo->frameCount)
				{
					request.Sample = null;
					return;
				}
				outputInfo->SetEstimatedTimeOfCompletion(currentVideoFrameIndex, outputInfo->frameCount);
				var videoFramePixels = outputInfo->GetVideoFramePixels(outputInfo->frameCount, (uint)currentVideoFrameIndex);
				var videoFrameBuffer = new Windows.Storage.Streams.Buffer((uint)(outputInfo->width * outputInfo->height * 2));
				new ReadOnlySpan<byte>(videoFramePixels, (int)videoFrameBuffer.Length).ToArray().AsSpan().CopyTo(videoFrameBuffer);
				request.Sample = MediaStreamSample.CreateFromBuffer(videoFrameBuffer, TimeSpan.FromSeconds((double)outputInfo->scale / outputInfo->rate * currentVideoFrameIndex));
				++currentVideoFrameIndex;
				return;
			}

			if (request.StreamDescriptor is AudioStreamDescriptor)
			{
				var audioSampleData = outputInfo->GetAudioSample(outputInfo->audioSamplingCount, outputInfo->audioChannelCount, out var actualSampleCount, (uint)currentAudioSampleIndex);
				if (actualSampleCount == 0)
				{
					request.Sample = null;
					return;
				}

				var audioSampleBuffer = new Windows.Storage.Streams.Buffer((uint)(actualSampleCount * outputInfo->audioChannelCount * 2));

				new ReadOnlySpan<byte>(audioSampleData, (int)audioSampleBuffer.Length).ToArray().AsSpan().CopyTo(audioSampleBuffer);
				request.Sample = MediaStreamSample.CreateFromBuffer(audioSampleBuffer, TimeSpan.FromSeconds((double)outputInfo->audioSamplingCount / outputInfo->audioRate));
				currentAudioSampleIndex += outputInfo->audioRate;
				return;
			}
		};

		Logger->LogInfo(Logger, Marshal.StringToHGlobalAuto("Output format is MP4."));
		var mediaEncodingProfile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);

		Logger->LogInfo(Logger, Marshal.StringToHGlobalAuto("Preparing media transcoder..."));
		var mediaTranscoder = new MediaTranscoder
		{
			HardwareAccelerationEnabled = true
		};

		Logger->LogInfo(Logger, Marshal.StringToHGlobalAuto("Preparing destination file..."));
		using var outputDestination = FileRandomAccessStream.OpenAsync
			(
			Marshal.PtrToStringAuto(outputInfo->destination),
			FileAccessMode.ReadWrite,
			StorageOpenOptions.AllowReadersAndWriters,
			FileOpenDisposition.CreateAlways
			)
			.GetAwaiter()
			.GetResult();
		Logger->LogInfo(Logger, Marshal.StringToHGlobalAuto("Working..."));
		var result = mediaTranscoder.PrepareMediaStreamSourceTranscodeAsync(mediaStreamSource, outputDestination, mediaEncodingProfile).GetAwaiter().GetResult();
		result.TranscodeAsync().GetAwaiter().GetResult();

		return true;
	}

	[UnmanagedCallersOnly(EntryPoint = "InitializeLogger", CallConvs = new Type[] { typeof(CallConvStdcall) })]
	public unsafe static void InitializeLogger(nint ptr)
	{
		Logger = (Logger*)ptr;
	}

	[UnmanagedCallersOnly(EntryPoint = "GetOutputPluginTable", CallConvs = new[] { typeof(CallConvStdcall) })]
	public unsafe static nint GetOutputPluginTable()
	{
		if (PluginTable is null)
		{
			PluginTable = (OutputPluginTable*)NativeMemory.Alloc((nuint)sizeof(OutputPluginTable));
			PluginTable->flags = (1 << 0) | (1 << 1);
			PluginTable->pluginName = Marshal.StringToHGlobalAuto("WinRTOutput");
			PluginTable->fileFilters = Marshal.StringToHGlobalAuto("MP4 (*.mp4)\0*.mp4\0");
			PluginTable->pluginDescription = Marshal.StringToHGlobalAuto("WinRTOutput");
			PluginTable->Output = &OutputFile;
		}

		return (nint)PluginTable;
	}
}