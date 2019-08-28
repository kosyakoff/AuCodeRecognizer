using Android.Hardware.Camera2;
using Java.IO;
using Java.Lang;

namespace Camera2Basic.Listeners
{
    public class CameraCaptureListener : CameraCaptureSession.CaptureCallback
    {
        private readonly Camera2BasicFragment _owner;

        public CameraCaptureListener(Camera2BasicFragment owner)
        {
            if (owner == null)
                throw new System.ArgumentNullException("owner");
            this._owner = owner;
        }

        public override void OnCaptureCompleted(CameraCaptureSession session, CaptureRequest request, TotalCaptureResult result)
        {
            Process(result);
        }

        public override void OnCaptureProgressed(CameraCaptureSession session, CaptureRequest request, CaptureResult partialResult)
        {
            Process(partialResult);
        }

        private void Process(CaptureResult result)
        {
            switch (_owner.CurrentState)
            {
                case Camera2BasicFragment.STATE_WAITING_LOCK:
                    {
                        Integer afState = (Integer)result.Get(CaptureResult.ControlAfState);
                        if (afState == null)
                        {
                            _owner.CaptureStillPicture();
                        }

                        else if ((((int)ControlAFState.FocusedLocked) == afState.IntValue()) ||
                                   (((int)ControlAFState.NotFocusedLocked) == afState.IntValue()))
                        {
                            // ControlAeState can be null on some devices
                            Integer aeState = (Integer)result.Get(CaptureResult.ControlAeState);
                            if (aeState == null ||
                                    aeState.IntValue() == ((int)ControlAEState.Converged))
                            {
                                _owner.CurrentState = Camera2BasicFragment.STATE_PICTURE_TAKEN;
                                _owner.CaptureStillPicture();
                            }
                            else
                            {
                                _owner.RunPrecaptureSequence();
                            }
                        }
                        break;
                    }
                case Camera2BasicFragment.STATE_WAITING_PRECAPTURE:
                    {
                        // ControlAeState can be null on some devices
                        Integer aeState = (Integer)result.Get(CaptureResult.ControlAeState);
                        if (aeState == null ||
                                aeState.IntValue() == ((int)ControlAEState.Precapture) ||
                                aeState.IntValue() == ((int)ControlAEState.FlashRequired))
                        {
                            _owner.CurrentState = Camera2BasicFragment.STATE_WAITING_NON_PRECAPTURE;
                        }
                        break;
                    }
                case Camera2BasicFragment.STATE_WAITING_NON_PRECAPTURE:
                    {
                        // ControlAeState can be null on some devices
                        Integer aeState = (Integer)result.Get(CaptureResult.ControlAeState);
                        if (aeState == null || aeState.IntValue() != ((int)ControlAEState.Precapture))
                        {
                            _owner.CurrentState = Camera2BasicFragment.STATE_PICTURE_TAKEN;
                            _owner.CaptureStillPicture();
                        }
                        break;
                    }
            }
        }
    }
}