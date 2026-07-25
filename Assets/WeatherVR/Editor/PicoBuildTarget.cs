namespace WeatherVR.EditorTools
{
    /// <summary>
    /// Which Android variant the current editor invocation is building. A single
    /// static flag, valid for the lifetime of one <c>BuildPipeline.BuildPlayer</c>
    /// call, because <see cref="PicoManifestPatcher"/> (an
    /// <c>IPostGenerateGradleAndroidProject</c> callback) runs for every Android
    /// variant and needs to know whether it should stamp the manifest as a PICO VR
    /// app.
    ///
    /// Defaults to <c>true</c>: the headset is the product this repo ships. Both
    /// phone build entry points set it <c>false</c> as their first statement.
    /// </summary>
    public static class PicoBuildTarget
    {
        public static bool IsPico = true;
    }
}
