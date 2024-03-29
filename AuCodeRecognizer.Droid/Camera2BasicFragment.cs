﻿using System;
using System.Collections.Generic;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using Android.Hardware.Camera2;
using Android.Graphics;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.Support.V13.App;
using Android.Support.V4.Content;
using Camera2Basic.Listeners;
using Java.IO;
using Java.Lang;
using Java.Util;
using Java.Util.Concurrent;
using Boolean = Java.Lang.Boolean;
using Math = Java.Lang.Math;
using Orientation = Android.Content.Res.Orientation;

namespace Camera2Basic
{
    public class Camera2BasicFragment : Fragment, FragmentCompat.IOnRequestPermissionsResultCallback
    {
        private static readonly SparseIntArray ORIENTATIONS = new SparseIntArray();
        public static readonly int REQUEST_CAMERA_PERMISSION = 1;
        public static readonly int REQUEST_WRITE_PERMISSION = 2;
        private static readonly string FRAGMENT_DIALOG = "dialog";

        // Tag for the {@link Log}.
        private static readonly string TAG = "Camera2BasicFragment";

        // Max preview width that is guaranteed by Camera2 API
        private static readonly int MAX_PREVIEW_WIDTH = 1920;

        // Max preview height that is guaranteed by Camera2 API
        private static readonly int MAX_PREVIEW_HEIGHT = 1080;

        // TextureView.ISurfaceTextureListener handles several lifecycle events on a TextureView
        private Camera2BasicSurfaceTextureListener _mSurfaceTextureListener;

        // ID of the current {@link CurrentCameraDevice}.
        private string _cameraId;

        // An AutoFitTextureView for camera preview
        private AutoFitTextureView _textureView;

        // A {@link CameraCaptureSession } for camera preview.
        public CameraCaptureSession CaptureSession { get; set; }

        // A reference to the opened CurrentCameraDevice
        public CameraDevice CurrentCameraDevice { get; set; }

        // The size of the camera preview
        private Size _previewSize;

        // CurrentCameraDevice.StateListener is called when a CurrentCameraDevice changes its state
        private CameraStateListener _stateCallback;

        // An additional thread for running tasks that shouldn't block the UI.
        private HandlerThread _backgroundThread;

        // A {@link Handler} for running tasks in the background.
        public Handler BackgroundHandler { get; private set; }

        //{@link CaptureRequest.Builder} for the camera preview
        public CaptureRequest.Builder PreviewRequestBuilder { get; set; }

        // A {@link CameraCaptureSession.CaptureCallback} that handles events related to JPEG capture.
        public CameraCaptureListener MCaptureCallback;

        // {@link CaptureRequest} generated by {@link #PreviewRequestBuilder}
        public CaptureRequest PreviewRequest { get; set; }

        // A {@link Semaphore} to prevent the app from exiting before closing the camera.
        public Semaphore CameraOpenCloseLock = new Semaphore(1);

        // Orientation of the camera sensor
        private int _sensorOrientation;

        // Shows a {@link Toast} on the UI thread.
        public void ShowToast(string text)
        {
            if (Activity != null)
            {
                Activity.RunOnUiThread(new ShowToastRunnable(Activity.ApplicationContext, text));
            }
        }

        private class ShowToastRunnable : Java.Lang.Object, IRunnable
        {
            private string text;
            private Context context;

            public ShowToastRunnable(Context context, string text)
            {
                this.context = context;
                this.text = text;
            }

            public void Run()
            {
                Toast.MakeText(context, text, ToastLength.Short).Show();
            }
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
                if ((option.Width <= maxWidth) && (option.Height <= maxHeight) &&
                       option.Height == option.Width * h / w)
                {
                    if (option.Width >= textureViewWidth &&
                        option.Height >= textureViewHeight)
                    {
                        bigEnough.Add(option);
                    }
                    else
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
                Log.Error(TAG, "Couldn't find any suitable preview size");
                return choices[0];
            }
        }

        public static Camera2BasicFragment NewInstance()
        {
            return new Camera2BasicFragment();
        }

        public override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            _stateCallback = new CameraStateListener(this);
            _mSurfaceTextureListener = new Camera2BasicSurfaceTextureListener(this);

            // fill ORIENTATIONS list
            ORIENTATIONS.Append((int)SurfaceOrientation.Rotation0, 90);
            ORIENTATIONS.Append((int)SurfaceOrientation.Rotation90, 0);
            ORIENTATIONS.Append((int)SurfaceOrientation.Rotation180, 270);
            ORIENTATIONS.Append((int)SurfaceOrientation.Rotation270, 180);
        }

        public override View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState)
        {
            return inflater.Inflate(Resource.Layout.fragment_camera2_basic, container, false);
        }

        public override void OnViewCreated(View view, Bundle savedInstanceState)
        {
            _textureView = (AutoFitTextureView)view.FindViewById(Resource.Id.texture);
        }

        public override void OnActivityCreated(Bundle savedInstanceState)
        {
            MCaptureCallback = new CameraCaptureListener(this);
            base.OnActivityCreated(savedInstanceState);
        }

        public override void OnResume()
        {
            base.OnResume();
            StartBackgroundThread();

            // When the screen is turned off and turned back on, the SurfaceTexture is already
            // available, and "onSurfaceTextureAvailable" will not be called. In that case, we can open
            // a camera and start preview from here (otherwise, we wait until the surface is ready in
            // the SurfaceTextureListener).
            if (_textureView.IsAvailable)
            {
                OpenCamera(_textureView.Width, _textureView.Height);
            }
            else
            {
                _textureView.SurfaceTextureListener = _mSurfaceTextureListener;
            }
        }

        public override void OnPause()
        {
            CloseCamera();
            StopBackgroundThread();
            base.OnPause();
        }

        private void RequestCameraPermission()
        {
            if (FragmentCompat.ShouldShowRequestPermissionRationale(this, Manifest.Permission.Camera))
            {
                new ConfirmationDialog().Show(ChildFragmentManager, FRAGMENT_DIALOG);
            }
            else
            {
                FragmentCompat.RequestPermissions(this, new string[] { Manifest.Permission.Camera },
                        REQUEST_CAMERA_PERMISSION);
            }

            if (FragmentCompat.ShouldShowRequestPermissionRationale(this, Manifest.Permission.WriteExternalStorage))
            {
                new ConfirmationDialog().Show(ChildFragmentManager, FRAGMENT_DIALOG);
            }
            else
            {
                FragmentCompat.RequestPermissions(this, new string[] { Manifest.Permission.WriteExternalStorage },
                    REQUEST_WRITE_PERMISSION);
            }
        }

        public void OnRequestPermissionsResult(int requestCode, string[] permissions, int[] grantResults)
        {
            if (requestCode != REQUEST_CAMERA_PERMISSION || requestCode != REQUEST_CAMERA_PERMISSION)
                return;

            if (grantResults.Length != 1 || grantResults[0] != (int)Permission.Granted)
            {
                ErrorDialog.NewInstance(GetString(Resource.String.request_permission))
                        .Show(ChildFragmentManager, FRAGMENT_DIALOG);
            }
        }


        // Sets up member variables related to camera.
        private void SetUpCameraOutputs(int width, int height)
        {
            var activity = Activity;
            var manager = (CameraManager)activity.GetSystemService(Context.CameraService);
            try
            {
                for (var i = 0; i < manager.GetCameraIdList().Length; i++)
                {
                    var cameraId = manager.GetCameraIdList()[i];
                    CameraCharacteristics characteristics = manager.GetCameraCharacteristics(cameraId);

                    // We don't use a front facing camera in this sample.
                    var facing = (Integer)characteristics.Get(CameraCharacteristics.LensFacing);
                    if (facing != null && facing == (Integer.ValueOf((int)LensFacing.Front)))
                    {
                        continue;
                    }

                    var map = (StreamConfigurationMap)characteristics.Get(CameraCharacteristics.ScalerStreamConfigurationMap);
                    if (map == null)
                    {
                        continue;
                    }

                    // For still image captures, we use the largest available size.
                    //Size largest = (Size)Collections.Max(Arrays.AsList(map.GetOutputSizes((int)ImageFormatType.Jpeg)),
                    //    new CompareSizesByArea());

                    // Find out if we need to swap dimension to get the preview size relative to sensor
                    // coordinate.
                    var displayRotation = activity.WindowManager.DefaultDisplay.Rotation;
                    //noinspection ConstantConditions
                    _sensorOrientation = (int)characteristics.Get(CameraCharacteristics.SensorOrientation);
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
                            Log.Error(TAG, "Display rotation is invalid: " + displayRotation);
                            break;
                    }

                    Point displaySize = new Point();
                    activity.WindowManager.DefaultDisplay.GetSize(displaySize);
                    var rotatedPreviewWidth = width;
                    var rotatedPreviewHeight = height;
                    var maxPreviewWidth = displaySize.X;
                    var maxPreviewHeight = displaySize.Y;

                    if (swappedDimensions)
                    {
                        rotatedPreviewWidth = height;
                        rotatedPreviewHeight = width;
                        maxPreviewWidth = displaySize.Y;
                        maxPreviewHeight = displaySize.X;
                    }

                    if (maxPreviewWidth > MAX_PREVIEW_WIDTH)
                    {
                        maxPreviewWidth = MAX_PREVIEW_WIDTH;
                    }

                    if (maxPreviewHeight > MAX_PREVIEW_HEIGHT)
                    {
                        maxPreviewHeight = MAX_PREVIEW_HEIGHT;
                    }

                    // Danger, W.R.! Attempting to use too large a preview size could  exceed the camera
                    // bus' bandwidth limitation, resulting in gorgeous previews but the storage of
                    // garbage capture data.
                    //_previewSize = ChooseOptimalSize(map.GetOutputSizes(Class.FromType(typeof(SurfaceTexture))),
                    //    rotatedPreviewWidth, rotatedPreviewHeight, maxPreviewWidth,
                    //    maxPreviewHeight, largest);
                    _previewSize = ChooseOptimalSize(map.GetOutputSizes(Class.FromType(typeof(SurfaceTexture))),
                        rotatedPreviewWidth, rotatedPreviewHeight, maxPreviewWidth,
                        maxPreviewHeight, new Size(maxPreviewWidth, maxPreviewHeight));

                    // We fit the aspect ratio of TextureView to the size of preview we picked.
                    var orientation = Resources.Configuration.Orientation;
                    if (orientation == Orientation.Landscape)
                    {
                        _textureView.SetAspectRatio(_previewSize.Width, _previewSize.Height);
                    }
                    else
                    {
                        _textureView.SetAspectRatio(_previewSize.Height, _previewSize.Width);
                    }

                    _cameraId = cameraId;
                    return;
                }
            }
            catch (CameraAccessException e)
            {
                e.PrintStackTrace();
            }
            catch (NullPointerException e)
            {
                // Currently an NPE is thrown when the Camera2API is used but not supported on the
                // device this code runs.
                ErrorDialog.NewInstance(GetString(Resource.String.camera_error)).Show(ChildFragmentManager, FRAGMENT_DIALOG);
            }
        }

        // Opens the camera specified by {@link Camera2BasicFragment#mCameraId}.
        public void OpenCamera(int width, int height)
        {
            if (ContextCompat.CheckSelfPermission(Activity, Manifest.Permission.Camera) != Permission.Granted)
            {
                RequestCameraPermission();
                return;
            }
            SetUpCameraOutputs(width, height);
            ConfigureTransform(width, height);
            var activity = Activity;
            var manager = (CameraManager)activity.GetSystemService(Context.CameraService);
            try
            {
                if (!CameraOpenCloseLock.TryAcquire(2500, TimeUnit.Milliseconds))
                {
                    throw new RuntimeException("Time out waiting to lock camera opening.");
                }
                manager.OpenCamera(_cameraId, _stateCallback, BackgroundHandler);
            }
            catch (CameraAccessException e)
            {
                e.PrintStackTrace();
            }
            catch (InterruptedException e)
            {
                throw new RuntimeException("Interrupted while trying to lock camera opening.", e);
            }
        }

        // Closes the current {@link CurrentCameraDevice}.
        private void CloseCamera()
        {
            try
            {
                CameraOpenCloseLock.Acquire();
                if (null != CaptureSession)
                {
                    CaptureSession.Close();
                    CaptureSession = null;
                }
                if (null != CurrentCameraDevice)
                {
                    CurrentCameraDevice.Close();
                    CurrentCameraDevice = null;
                }
            }
            catch (InterruptedException e)
            {
                throw new RuntimeException("Interrupted while trying to lock camera closing.", e);
            }
            finally
            {
                CameraOpenCloseLock.Release();
            }
        }

        // Starts a background thread and its {@link Handler}.
        private void StartBackgroundThread()
        {
            _backgroundThread = new HandlerThread("CameraBackground");
            _backgroundThread.Start();
            BackgroundHandler = new Handler(_backgroundThread.Looper);
        }

        // Stops the background thread and its {@link Handler}.
        private void StopBackgroundThread()
        {
            _backgroundThread.QuitSafely();
            try
            {
                _backgroundThread.Join();
                _backgroundThread = null;
                BackgroundHandler = null;
            }
            catch (InterruptedException e)
            {
                e.PrintStackTrace();
            }
        }

        // Creates a new {@link CameraCaptureSession} for camera preview.
        public void CreateCameraPreviewSession()
        {
            try
            {
                SurfaceTexture texture = _textureView.SurfaceTexture;
                if (texture == null)
                {
                    throw new IllegalStateException("texture is null");
                }

                // We configure the size of default buffer to be the size of camera preview we want.
                texture.SetDefaultBufferSize(_previewSize.Width, _previewSize.Height);

                // This is the output Surface we need to start preview.
                Surface surface = new Surface(texture);

                // We set up a CaptureRequest.Builder with the output Surface.
                PreviewRequestBuilder = CurrentCameraDevice.CreateCaptureRequest(CameraTemplate.Preview);
                PreviewRequestBuilder.AddTarget(surface);

                // Here, we create a CameraCaptureSession for camera preview.
                List<Surface> surfaces = new List<Surface>();
                surfaces.Add(surface);
                CurrentCameraDevice.CreateCaptureSession(surfaces, new CameraCaptureSessionCallback(this), null);

            }
            catch (CameraAccessException e)
            {
                e.PrintStackTrace();
            }
        }

        public static T Cast<T>(Java.Lang.Object obj) where T : class
        {
            var propertyInfo = obj.GetType().GetProperty("Instance");
            return propertyInfo == null ? null : propertyInfo.GetValue(obj, null) as T;
        }

        // Configures the necessary {@link android.graphics.Matrix}
        // transformation to `_textureView`.
        // This method should be called after the camera preview size is determined in
        // setUpCameraOutputs and also the size of `_textureView` is fixed.

        public void ConfigureTransform(int viewWidth, int viewHeight)
        {
            Activity activity = Activity;
            if (null == _textureView || null == _previewSize || null == activity)
            {
                return;
            }
            var rotation = (int)activity.WindowManager.DefaultDisplay.Rotation;
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
            _textureView.SetTransform(matrix);
        }

        // Retrieves the JPEG orientation from the specified screen rotation.
        private int GetOrientation(int rotation)
        {
            // Sensor orientation is 90 for most devices, or 270 for some devices (eg. Nexus 5X)
            // We have to take that into account and rotate JPEG properly.
            // For devices with orientation of 90, we simply return our mapping from ORIENTATIONS.
            // For devices with orientation of 270, we need to rotate the JPEG 180 degrees.
            return (ORIENTATIONS.Get(rotation) + _sensorOrientation + 270) % 360;
        }
    }
}

