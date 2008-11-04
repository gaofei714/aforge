﻿// AForge Image Processing Library
// AForge.NET framework
//
// Copyright © Andrew Kirillov, 2005-2008
// andrew.kirillov@gmail.com
//
// Alejandro Pirola, 2008
// alejamp@gmail.com
//

namespace AForge.Imaging
{
    using System;
    using System.Collections;
    using System.Drawing;
    using System.Drawing.Imaging;
    
    /// <summary>
    /// Skew angle checker for scanned documents.
    /// </summary>
    ///
    /// <remarks>
    /// 
    /// </remarks>
    ///
    public class DocumentSkewChecker
    {
        // Hough transformation: quality settings
        private int     stepsPerDegree;
        private int     houghHeight;
        private double  thetaStep;
        private double  minTheta;
        private double  maxTheta;

        // Hough transformation: precalculated Sine and Cosine values
        private double[]	sinMap;
        private double[]	cosMap;
        private bool        needToInitialize = true;

        // Hough transformation: Hough map
        private short[,]	houghMap;
        private short		maxMapIntensity = 0;

        private int 		localPeakRadius = 4;
        private ArrayList   lines = new ArrayList( );

        /// <summary>
        /// Steps per degree, [1, 10].
        /// </summary>
        /// 
        /// <remarks><para>The value defines quality of Hough transform and its ability to detect
        /// line slope precisely.</para>
        /// 
        /// <para>Default value is <b>1</b>.</para>
        /// </remarks>
        /// 
        public int StepsPerDegree
        {
            get { return stepsPerDegree; }
            set
            {
                stepsPerDegree = Math.Max( 1, Math.Min( 10, value ) );
                needToInitialize = true;
            }
        }

        /// <summary>
        /// Minimum angle to detect skew in degrees.
        /// </summary>
        ///
        /// <remarks><para>The value sets minimum angle for a line to detect skew of. 
        /// Scanned mages usualy have a skew between in the range of [-20, 20] degrees.</para>
        /// 
        /// <para>Default value is <b>-30</b>.</para></remarks>
        ///
        public double MinBeta
        {
            get { return ( 90 - minTheta ); }
            set
            {
                minTheta = 90 + value;
                needToInitialize = true;
            }
        }

        /// <summary>
        /// Maximum angle to detect skew in degrees.
        /// </summary>
        ///
        /// <remarks><para>The value sets maximum angle for a line to detect skew of. 
        /// Scanned mages usualy have a skew between in the range of [-20, 20] degrees.</para>
        /// 
        /// <para>Default value is <b>30</b> degrees.</para></remarks>
        ///
        public double MaxBeta
        {
            get { return ( 90 - maxTheta ); }
            set
            {
                maxTheta = 90 + value;
                needToInitialize = true;
            }
        }

        /// <summary>
        /// Radius for searching local peak value, [1, 10].
        /// </summary>
        /// 
        /// <remarks><para>The value determines radius around a map's value, which is analyzed to determine
        /// if the map's value is a local maximum in specified area.</para>
        /// 
        /// <para>Default value is set to <b>4</b>.</para></remarks>
        /// 
        public int LocalPeakRadius
        {
            get { return localPeakRadius; }
            set { localPeakRadius = Math.Max( 1, Math.Min( 10, value ) ); }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DocumentSkewChecker"/> class.
        /// </summary>
        public DocumentSkewChecker( )
        {
            StepsPerDegree = 5;
            MinBeta = -30;
            MaxBeta =  30;
        }

        /// <summary>
        /// Get skew angle of the provided document image.
        /// </summary>
        /// 
        /// <param name="image">Document's image to get skew angle of.</param>
        /// 
        /// <returns>Returns document's skew angle.</returns>
        /// 
        /// <exception cref="ArgumentException">Unsupported pixel format of the source image.</exception>
        /// 
        public double GetSkewAngle( Bitmap image )
        {
            // check image format
            if ( image.PixelFormat != PixelFormat.Format8bppIndexed )
            {
                throw new ArgumentException( "Unsupported pixel format of the source image." );
            }

            // lock source image
            BitmapData imageData = image.LockBits(
                new Rectangle( 0, 0, image.Width, image.Height ),
                ImageLockMode.ReadOnly, PixelFormat.Format8bppIndexed );

            // process the image
            double skewAngle = GetSkewAngle( new UnmanagedImage( imageData ) );

            // unlock image
            image.UnlockBits( imageData );

            return skewAngle;
        }

        /// <summary>
        /// Get skew angle of the provided document image.
        /// </summary>
        /// 
        /// <param name="imageData">Document's image data to get skew angle of.</param>
        /// 
        /// <returns>Returns document's skew angle.</returns>
        /// 
        /// <exception cref="ArgumentException">Unsupported pixel format of the source image.</exception>
        /// 
        public double GetSkewAngle( BitmapData imageData )
        {
            return GetSkewAngle( new UnmanagedImage( imageData ) );
        }

        /// <summary>
        /// Get skew angle of the provided document image.
        /// </summary>
        /// 
        /// <param name="image">Document's unmanaged image to get skew angle of.</param>
        /// 
        /// <returns>Returns document's skew angle.</returns>
        /// 
        /// <exception cref="ArgumentException">Unsupported pixel format of the source image.</exception>
        /// 
        public double GetSkewAngle( UnmanagedImage image )
        {
            if ( image.PixelFormat != PixelFormat.Format8bppIndexed )
            {
                throw new ArgumentException( "Unsupported pixel format of the source image." );
            }

            // init hough transformation settings
            InitHoughMap( );

            // get source image size
            int width       = image.Width;
            int height      = image.Height;
            int offset      = image.Stride - width;
            int halfWidth   = width / 2;
            int halfHeight  = height / 2;
            int toWidth     = width - halfWidth;
            int toHeight    = height - halfHeight - 1;

            // calculate Hough map's width
            int halfHoughWidth = (int) Math.Sqrt( halfWidth * halfWidth + halfHeight * halfHeight );
            int houghWidth = halfHoughWidth * 2;

            houghMap = new short[houghHeight, houghWidth];

            // do the job
            unsafe
            {
                byte* src = (byte*) image.ImageData.ToPointer( );
                byte* srcBelow = (byte*) image.ImageData.ToPointer( ) + image.Stride;

                // for each row
                for ( int y = -halfHeight; y < toHeight; y++ )
                {
                    // for each pixel
                    for ( int x = -halfWidth; x < toWidth; x++, src++ )
                    {
                        // if current pixel is more black
                        // and pixel below is more white
                        if ( ( *src < 100 ) && ( ( *src < (byte) ( *srcBelow * 0.5 ) ) ) )
                        {
                            // for each Theta value
                            for ( int theta = 0; theta < houghHeight; theta++ )
                            {
                                int radius = (int) ( cosMap[theta] * x - sinMap[theta] * y ) + halfHoughWidth;

                                if ( ( radius < 0 ) || ( radius >= houghWidth ) )
                                    continue;

                                houghMap[theta, radius]++;
                            }
                        }
                    }
                    src += offset;
                    srcBelow += offset;
                }
            }

            // find max value in Hough map
            maxMapIntensity = 0;
            for ( int i = 0; i < houghHeight; i++ )
            {
                for ( int j = 0; j < houghWidth; j++ )
                {
                    if ( houghMap[i, j] > maxMapIntensity )
                    {
                        maxMapIntensity = houghMap[i, j];
                    }
                }
            }

            CollectLines( (short) ( width / 10 ) );

            // get skew angle
            HoughLine[] hls = this.GetMostIntensiveLines( 10 );

            double skewAngle = 0;
            if ( hls != null )
            {
                foreach ( HoughLine hl in hls )
                {
                    skewAngle += hl.Theta;
                }
                if ( hls.Length > 0 ) skewAngle = skewAngle / hls.Length;
            }

            return skewAngle - 90.0;
        }

        // Get specified amount of lines with highest intensity
        private HoughLine[] GetMostIntensiveLines( int count )
        {
            // lines count
            int n = Math.Min( count, lines.Count );

            if ( n == 0 )
                return null;

            // result array
            HoughLine[] dst = new HoughLine[n];
            lines.CopyTo( 0, dst, 0, n );

            return dst;
        }

        // Collect lines with intesities greater or equal then specified
        private void CollectLines( short minLineIntensity )
        {
            int		maxTheta = houghMap.GetLength( 0 );
            int		maxRadius = houghMap.GetLength( 1 );

            short	intensity;
            bool	foundGreater;

            int     halfHoughWidth = maxRadius >> 1;

            // clean lines collection
            lines.Clear( );

            // for each Theta value
            for ( int theta = 0; theta < maxTheta; theta++ )
            {
                // for each Radius value
                for ( int radius = 0; radius < maxRadius; radius++ )
                {
                    // get current value
                    intensity = houghMap[theta, radius];

                    if ( intensity < minLineIntensity )
                        continue;

                    foundGreater = false;

                    // check neighboors
                    for ( int tt = theta - localPeakRadius, ttMax = theta + localPeakRadius; tt < ttMax; tt++ )
                    {
                        // break if it is not local maximum
                        if ( foundGreater == true )
                            break;

                        int cycledTheta = tt;
                        int cycledRadius = radius;

                        // check limits
                        if ( cycledTheta < 0 )
                        {
                            cycledTheta = maxTheta + cycledTheta;
                            cycledRadius = maxRadius - cycledRadius;
                        }
                        if ( cycledTheta >= maxTheta )
                        {
                            cycledTheta -= maxTheta;
                            cycledRadius = maxRadius - cycledRadius;
                        }

                        for ( int tr = cycledRadius - localPeakRadius, trMax = cycledRadius + localPeakRadius; tr < trMax; tr++ )
                        {
                            // skip out of map values
                            if ( tr < 0 )
                                continue;
                            if ( tr >= maxRadius )
                                break;

                            // compare the neighboor with current value
                            if ( houghMap[cycledTheta, tr] > intensity )
                            {
                                foundGreater = true;
                                break;
                            }
                        }
                    }

                    // was it local maximum ?
                    if ( !foundGreater )
                    {
                        // we have local maximum
                        lines.Add( new HoughLine( minTheta + (double) theta / stepsPerDegree, (short) ( radius - halfHoughWidth ), intensity, (double) intensity / maxMapIntensity ) );
                    }
                }
            }

            lines.Sort( );
        }

        // Init Hough settings and map
        private void InitHoughMap( )
        {
            if ( needToInitialize )
            {
                needToInitialize = false;

                houghHeight = (int) ( ( maxTheta - minTheta ) * stepsPerDegree );
                thetaStep = ( ( maxTheta - minTheta ) * Math.PI / 180 ) / houghHeight;

                // precalculate Sine and Cosine values
                sinMap = new double[houghHeight];
                cosMap = new double[houghHeight];

                for ( int i = 0; i < houghHeight; i++ )
                {
                    sinMap[i] = Math.Sin( ( minTheta * Math.PI / 180 ) + ( i * thetaStep ) );
                    cosMap[i] = Math.Cos( ( minTheta * Math.PI / 180 ) + ( i * thetaStep ) );
                }
            }
        }
    }
}