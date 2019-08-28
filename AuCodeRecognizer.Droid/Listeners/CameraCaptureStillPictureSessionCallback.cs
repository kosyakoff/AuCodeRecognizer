using Android.Hardware.Camera2;
using Android.Util;

namespace Camera2Basic.Listeners
{
    public class CameraCaptureStillPictureSessionCallback : CameraCaptureSession.CaptureCallback
    {
        private static readonly string TAG = "CameraCaptureStillPictureSessionCallback";

        private readonly Camera2BasicFragment _owner;

        public CameraCaptureStillPictureSessionCallback(Camera2BasicFragment owner)
        {
            if (owner == null)
                throw new System.ArgumentNullException("owner");
            this._owner = owner;
        }

        public override void OnCaptureCompleted(CameraCaptureSession session, CaptureRequest request, TotalCaptureResult result)
        {
            // If something goes wrong with the save (or the handler isn't even 
            // registered, this code will toast a success message regardless...)
            _owner.ShowToast("Saved: " + _owner.FileToSave);
            Log.Debug(TAG, _owner.FileToSave.ToString());
            _owner.UnlockFocus();
        }
    }
}
