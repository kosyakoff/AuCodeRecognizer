using Android.App;
using Android.Hardware.Camera2;

namespace Camera2Basic.Listeners
{
    public class CameraStateListener : CameraDevice.StateCallback
    {
        private readonly Camera2BasicFragment _owner;

        public CameraStateListener(Camera2BasicFragment owner)
        {
            if (owner == null)
                throw new System.ArgumentNullException("owner");
            this._owner = owner;
        }

        public override void OnOpened(CameraDevice cameraDevice)
        {
            // This method is called when the camera is opened.  We start camera preview here.
            _owner.CameraOpenCloseLock.Release();
            _owner.CurrentCameraDevice = cameraDevice;
            _owner.CreateCameraPreviewSession();
        }

        public override void OnDisconnected(CameraDevice cameraDevice)
        {
            _owner.CameraOpenCloseLock.Release();
            cameraDevice.Close();
            _owner.CurrentCameraDevice = null;
        }

        public override void OnError(CameraDevice cameraDevice, CameraError error)
        {
            _owner.CameraOpenCloseLock.Release();
            cameraDevice.Close();
            _owner.CurrentCameraDevice = null;
            if (_owner == null)
                return;
            Activity activity = _owner.Activity;
            if (activity != null)
            {
                activity.Finish();
            }
        }
    }
}