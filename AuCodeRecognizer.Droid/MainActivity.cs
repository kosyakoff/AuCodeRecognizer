using Android.App;
using Android.OS;
using Android.Support.V7.App;
using Android.Runtime;
using Android.Widget;
using AndroidCamera2Demo.Controls;
using Android.Content.PM;
using AndroidCamera2Demo.Callbacks;
using Android.Hardware.Camera2;
using Android.Views;
using Android.Util;
using System;
using Android.Hardware.Camera2.Params;
using Java.Util;
using Android.Graphics;
using Android.Media;
using System.Collections.Generic;
using System.Linq;

namespace AndroidCamera2Demo
{
    [Activity(Label = "@string/app_name", Theme = "@style/AppTheme", MainLauncher = true, ScreenOrientation = ScreenOrientation.Portrait)]
    public partial class MainActivity : AppCompatActivity
    {
        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            // Set our view from the "main" layout resource
            SetContentView(Resource.Layout.activity_main);

            _surfaceTextureView = FindViewById<AutoFitTextureView>(Resource.Id.surface);
            _switchCameraButton = FindViewById<ImageButton>(Resource.Id.reverse_camera_button);
            _takePictureButton = FindViewById<Button>(Resource.Id.take_picture_button);
            _recordVideoButton = FindViewById<Button>(Resource.Id.record_video_button);
            
            _cameraStateCallback = new CameraStateCallback
            {
                Opened = OnOpened,
                Disconnected = OnDisconnected,
                Error = OnError,
            };
            _captureStateSessionCallback = new CaptureStateSessionCallback
            {
                Configured = OnPreviewSessionConfigured,
            };
            _videoSessionStateCallback = new CaptureStateSessionCallback
            {
                Configured = OnVideoSessionConfigured,
            };
            _cameraCaptureCallback = new CameraCaptureCallback
            {
                CaptureCompleted = (session, request, result) => ProcessImageCapture(result),
                CaptureProgressed = (session, request, result) => ProcessImageCapture(result),
            };
            _manager = GetSystemService(CameraService) as CameraManager;
            _windowManager = GetSystemService(WindowService).JavaCast<IWindowManager>();
            _onImageAvailableListener = new ImageAvailableListener
            {
                ImageAvailable = HandleImageCaptured,
            };
            _orientations.Append((int)SurfaceOrientation.Rotation0, 90);
            _orientations.Append((int)SurfaceOrientation.Rotation90, 0);
            _orientations.Append((int)SurfaceOrientation.Rotation180, 270);
            _orientations.Append((int)SurfaceOrientation.Rotation270, 180);
        }

        private AutoFitTextureView _surfaceTextureView;
        private ImageButton _switchCameraButton;
        private Button _takePictureButton;
        private Button _recordVideoButton;
        private CameraStateCallback _cameraStateCallback;
        private CaptureStateSessionCallback _captureStateSessionCallback;
        private CaptureStateSessionCallback _videoSessionStateCallback;
        private CameraCaptureCallback _cameraCaptureCallback;
        private CameraManager _manager;
        private IWindowManager _windowManager;
        private ImageAvailableListener _onImageAvailableListener;
        private readonly SparseIntArray _orientations = new SparseIntArray();
        private LensFacing _currentLensFacing = LensFacing.Back;
        private CameraCharacteristics _characteristics;
        private CameraDevice _cameraDevice;
        private ImageReader _imageReader;
        private int _sensorOrientation;
        private Size _previewSize;
        private HandlerThread _backgroundThread;
        private Handler _backgroundHandler;
        private bool _flashSupported;
        private Surface _previewSurface;
        private CameraCaptureSession _captureSession;
        private CaptureRequest.Builder _previewRequestBuilder;
        private CaptureRequest _previewRequest;

        protected override void OnResume()
        {
            base.OnResume();
            _switchCameraButton.Click += SwitchCameraButton_Click;
            _takePictureButton.Click += TakePictureButton_Click;
            _recordVideoButton.Click += RecordVideoButton_Click;

            StartBackgroundThread();

            if (_surfaceTextureView.IsAvailable)
            {
                ForceResetLensFacing();
            }
            else
            {
                _surfaceTextureView.SurfaceTextureAvailable += SurfaceTextureView_SurfaceTextureAvailable;
            }
        }

        private void SurfaceTextureView_SurfaceTextureAvailable(object sender, TextureView.SurfaceTextureAvailableEventArgs e)
        {
            ForceResetLensFacing();
        }

        private void StartBackgroundThread()
        {
            _backgroundThread = new HandlerThread("CameraBackground");
            _backgroundThread.Start();
            _backgroundHandler = new Handler(_backgroundThread.Looper);
        }

        private void SwitchCameraButton_Click(object sender, EventArgs e)
        {
            SetLensFacing(_currentLensFacing == LensFacing.Back ? LensFacing.Front : LensFacing.Back);
        }

        protected override void OnPause()
        {
            base.OnPause();
            _switchCameraButton.Click -= SwitchCameraButton_Click;
            _takePictureButton.Click -= TakePictureButton_Click;
            _recordVideoButton.Click -= RecordVideoButton_Click;
            _surfaceTextureView.SurfaceTextureAvailable -= SurfaceTextureView_SurfaceTextureAvailable;

            CloseCamera();
            StopBackgroundThread();
        }

        void CloseCamera()
        {
            try
            {
                if (null != _captureSession)
                {
                    _captureSession.Close();
                    _captureSession = null;
                }
                if (null != _cameraDevice)
                {
                    _cameraDevice.Close();
                    _cameraDevice = null;
                }
                if (null != _imageReader)
                {
                    _imageReader.Close();
                    _imageReader = null;
                }
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine($"{e.Message} {e.StackTrace}");
            }
        }

        private void StopBackgroundThread()
        {
            if (_backgroundThread == null) return;

            _backgroundThread.QuitSafely();
            try
            {
                _backgroundThread.Join();
                _backgroundThread = null;
                _backgroundHandler = null;
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine($"{e.Message} {e.StackTrace}");
            }
        }

        /// <summary>
        /// This method forces our view to re-create the camera session by changing 'currentLensFacing' and requesting the original value
        /// </summary>
        private void ForceResetLensFacing()
        {
            var targetLensFacing = _currentLensFacing;
            _currentLensFacing = _currentLensFacing == LensFacing.Back ? LensFacing.Front : LensFacing.Back;
            SetLensFacing(targetLensFacing);
        }

        private void SetLensFacing(LensFacing lenseFacing)
        {
            bool shouldRestartCamera = _currentLensFacing != lenseFacing;
            _currentLensFacing = lenseFacing;
            string cameraId = string.Empty;
            _characteristics = null;

            foreach (var id in _manager.GetCameraIdList())
            {
                cameraId = id;
                _characteristics = _manager.GetCameraCharacteristics(id);

                var face = (int)_characteristics.Get(CameraCharacteristics.LensFacing);
                if (face == (int)_currentLensFacing)
                {
                    break;
                }
            }

            if (_characteristics == null) return;

            if (_cameraDevice != null)
            {
                try
                {
                    if (!shouldRestartCamera)
                        return;
                    if (_cameraDevice.Handle != IntPtr.Zero)
                    {
                        _cameraDevice.Close();
                        _cameraDevice.Dispose();
                        _cameraDevice = null;
                    }
                }
                catch (Exception e)
                {
                    //Ignored
                    System.Diagnostics.Debug.WriteLine(e);
                }
            }

            SetUpCameraOutputs(cameraId);
            ConfigureTransform(_surfaceTextureView.Width, _surfaceTextureView.Height);
            _manager.OpenCamera(cameraId, _cameraStateCallback, null);
        }

        private void SetUpCameraOutputs(string selectedCameraId)
        {
            var map = (StreamConfigurationMap)_characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
            if (map == null)
            {
                return;
            }

            // For still image captures, we use the largest available size.
            Size largest = (Size)Collections.Max(Arrays.AsList(map.GetOutputSizes((int)ImageFormatType.Jpeg)),
                new CompareSizesByArea());

            if (_imageReader == null)
            {
                _imageReader = ImageReader.NewInstance(largest.Width, largest.Height, ImageFormatType.Jpeg, maxImages: 1);
                _imageReader.SetOnImageAvailableListener(_onImageAvailableListener, _backgroundHandler);
            }

            // Find out if we need to swap dimension to get the preview size relative to sensor
            // coordinate.
            var displayRotation = _windowManager.DefaultDisplay.Rotation;
            _sensorOrientation = (int)_characteristics.Get(CameraCharacteristics.SensorOrientation);
            bool swappedDimensions = false;
            switch (displayRotation)
            {
                case SurfaceOrientation.Rotation0:
                case SurfaceOrientation.Rotation180:
                    if (_sensorOrientation == 90 || _sensorOrientation == 270)
                    {
                        swappedDimensions = true;
                    }
                    break;
                case SurfaceOrientation.Rotation90:
                case SurfaceOrientation.Rotation270:
                    if (_sensorOrientation == 0 || _sensorOrientation == 180)
                    {
                        swappedDimensions = true;
                    }
                    break;
                default:
                    System.Diagnostics.Debug.WriteLine($"Display rotation is invalid: {displayRotation}");
                    break;
            }

            Point displaySize = new Point();
            _windowManager.DefaultDisplay.GetSize(displaySize);
            var rotatedPreviewWidth = _surfaceTextureView.Width;
            var rotatedPreviewHeight = _surfaceTextureView.Height;
            var maxPreviewWidth = displaySize.X;
            var maxPreviewHeight = displaySize.Y;

            if (swappedDimensions)
            {
                rotatedPreviewWidth = _surfaceTextureView.Height;
                rotatedPreviewHeight = _surfaceTextureView.Width;
                maxPreviewWidth = displaySize.Y;
                maxPreviewHeight = displaySize.X;
            }

            // Danger, W.R.! Attempting to use too large a preview size could  exceed the camera
            // bus' bandwidth limitation, resulting in gorgeous previews but the storage of
            // garbage capture data.
            _previewSize = ChooseOptimalSize(map.GetOutputSizes(Java.Lang.Class.FromType(typeof(SurfaceTexture))),
                rotatedPreviewWidth, rotatedPreviewHeight, maxPreviewWidth,
                maxPreviewHeight, largest);

            // We fit the aspect ratio of TextureView to the size of preview we picked.
            // The commented code handles landscape layouts. This app is portrait only, so this is not needed
            /*
            var orientation = Application.Context.Resources.Configuration.Orientation;
            if (orientation == global::Android.Content.Res.Orientation.Landscape)
            {
                _surfaceTextureView.SetAspectRatio(previewSize.Width, previewSize.Height);
            }
            else
            {*/
                _surfaceTextureView.SetAspectRatio(_previewSize.Height, _previewSize.Width);
            /*}*/

            // Check if the flash is supported.
            var available = (bool?)_characteristics.Get(CameraCharacteristics.FlashInfoAvailable);
            if (available == null)
            {
                _flashSupported = false;
            }
            else
            {
                _flashSupported = (bool)available;
            }
            return;
        }

        // Configures the necessary matrix
        // transformation to `_surfaceTextureView`.
        // This method should be called after the camera preview size is determined in
        // setUpCameraOutputs and also the size of `_surfaceTextureView` is fixed.
        public void ConfigureTransform(int viewWidth, int viewHeight)
        {
            if (null == _surfaceTextureView || null == _previewSize)
            {
                return;
            }
            var rotation = (int)WindowManager.DefaultDisplay.Rotation;
            Matrix matrix = new Matrix();
            RectF viewRect = new RectF(0, 0, viewWidth, viewHeight);
            RectF bufferRect = new RectF(0, 0, _previewSize.Height, _previewSize.Width);
            float centerX = viewRect.CenterX();
            float centerY = viewRect.CenterY();
            if ((int)SurfaceOrientation.Rotation90 == rotation || (int)SurfaceOrientation.Rotation270 == rotation)
            {
                bufferRect.Offset(centerX - bufferRect.CenterX(), centerY - bufferRect.CenterY());
                matrix.SetRectToRect(viewRect, bufferRect, Matrix.ScaleToFit.Fill);
                float scale = Math.Max((float)viewHeight / _previewSize.Height, (float)viewWidth / _previewSize.Width);
                matrix.PostScale(scale, scale, centerX, centerY);
                matrix.PostRotate(90 * (rotation - 2), centerX, centerY);
            }
            else if ((int)SurfaceOrientation.Rotation180 == rotation)
            {
                matrix.PostRotate(180, centerX, centerY);
            }
            _surfaceTextureView.SetTransform(matrix);
        }

        private static Size ChooseOptimalSize(Size[] choices, int textureViewWidth,
            int textureViewHeight, int maxWidth, int maxHeight, Size aspectRatio)
        {
            // Collect the supported resolutions that are at least as big as the preview Surface
            var bigEnough = new List<Size>();
            // Collect the supported resolutions that are smaller than the preview Surface
            var notBigEnough = new List<Size>();
            int w = aspectRatio.Width;
            int h = aspectRatio.Height;

            for (var i = 0; i < choices.Length; i++)
            {
                Size option = choices[i];
                if (option.Height == option.Width * h / w)
                {
                    if (option.Width >= textureViewWidth &&
                        option.Height >= textureViewHeight)
                    {
                        bigEnough.Add(option);
                    }
                    else if ((option.Width <= maxWidth) && (option.Height <= maxHeight))
                    {
                        notBigEnough.Add(option);
                    }
                }
            }

            // Pick the smallest of those big enough. If there is no one big enough, pick the
            // largest of those not big enough.
            if (bigEnough.Count > 0)
            {
                return (Size)Collections.Min(bigEnough, new CompareSizesByArea());
            }
            else if (notBigEnough.Count > 0)
            {
                return (Size)Collections.Max(notBigEnough, new CompareSizesByArea());
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("Couldn't find any suitable preview size");
                return choices[0];
            }
        }

        private void OnOpened(CameraDevice cameraDevice)
        {
            this._cameraDevice = cameraDevice;
            _surfaceTextureView.SurfaceTexture.SetDefaultBufferSize(_previewSize.Width, _previewSize.Height);
            _previewSurface = new Surface(_surfaceTextureView.SurfaceTexture);

            this._cameraDevice.CreateCaptureSession(new List<Surface> { _previewSurface, _imageReader.Surface }, _captureStateSessionCallback, _backgroundHandler);
        }

        private void OnDisconnected(CameraDevice cameraDevice)
        {
            // In a real application we may need to handle the user disconnecting external devices.
            // Here we're only worrying about built-in cameras
        }

        private void OnError(CameraDevice cameraDevice, CameraError cameraError)
        {
            // In a real application we should handle errors gracefully
        }

        private void OnPreviewSessionConfigured(CameraCaptureSession session)
        {
            _captureSession = session;

            _previewRequestBuilder = _cameraDevice.CreateCaptureRequest(CameraTemplate.Preview);
            _previewRequestBuilder.AddTarget(_previewSurface);

            var availableAutoFocusModes = (int[])_characteristics.Get(CameraCharacteristics.ControlAfAvailableModes);
            if (availableAutoFocusModes.Any(afMode => afMode == (int)ControlAFMode.ContinuousPicture))
            {
                _previewRequestBuilder.Set(CaptureRequest.ControlAfMode, (int)ControlAFMode.ContinuousPicture);
            }
            SetAutoFlash(_previewRequestBuilder);

            _previewRequest = _previewRequestBuilder.Build();

            _captureSession.SetRepeatingRequest(_previewRequest, _cameraCaptureCallback, _backgroundHandler);
        }

        public void SetAutoFlash(CaptureRequest.Builder requestBuilder)
        {
            if (_flashSupported)
            {
                requestBuilder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.OnAutoFlash);
            }
        }

        /// <summary>
        /// Sensor orientation is 90 for most devices, or 270 for some devices (eg. Nexus 5X)
        /// We have to take that into account and rotate image properly.
        /// For devices with orientation of 90, we simply return our mapping from orientations.
        /// For devices with orientation of 270, we need to rotate 180 degrees. 
        /// </summary>
        int GetOrientation(int rotation) => (_orientations.Get(rotation) + _sensorOrientation + 270) % 360;
    }
}