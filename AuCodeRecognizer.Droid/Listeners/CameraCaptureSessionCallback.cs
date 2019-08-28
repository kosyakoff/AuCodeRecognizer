using Android.Hardware.Camera2;

namespace Camera2Basic.Listeners
{
    public class CameraCaptureSessionCallback : CameraCaptureSession.StateCallback
    {
        private readonly Camera2BasicFragment _owner;

        public CameraCaptureSessionCallback(Camera2BasicFragment owner)
        {
            if (owner == null)
                throw new System.ArgumentNullException("owner");
            this._owner = owner;
        }

        public override void OnConfigureFailed(CameraCaptureSession session)
        {
            _owner.ShowToast("Failed");
        }

        public override void OnConfigured(CameraCaptureSession session)
        {
            // The camera is already closed
            if (null == _owner.CameraDevice)
            {
                return;
            }

            // When the session is ready, we start displaying the preview.
            _owner.CaptureSession = session;
            try
            {
                // Auto focus should be continuous for camera preview.
                _owner.PreviewRequestBuilder.Set(CaptureRequest.ControlAfMode, (int)ControlAFMode.ContinuousPicture);
                // Flash is automatically enabled when necessary.
                _owner.SetAutoFlash(_owner.PreviewRequestBuilder);

                // Finally, we start displaying the camera preview.
                _owner.PreviewRequest = _owner.PreviewRequestBuilder.Build();
                _owner.CaptureSession.SetRepeatingRequest(_owner.PreviewRequest,
                        _owner.MCaptureCallback, _owner.MBackgroundHandler);
            }
            catch (CameraAccessException e)
            {
                e.PrintStackTrace();
            }
        }
    }
}