/////////////////////////////////////////////////////////////////////
//
//	QR Code Library
//
//	QR Code finder class.
//
//	Author: Uzi Granot
//	Original Version: 1.0
//	Date: June 30, 2018
//	Copyright (C) 2018-2019 Uzi Granot. All Rights Reserved
//	For full version history please look at QRDecoder.cs
//
//	QR Code Library C# class library and the attached test/demo
//  applications are free software.
//	Software developed by this author is licensed under CPOL 1.02.
//	Some portions of the QRCodeVideoDecoder are licensed under GNU Lesser
//	General Public License v3.0.
//
//	The solution is made of 3 projects:
//	1. QRCodeDecoderLibrary: QR code decoding.
//	3. QRCodeDecoderDemo: Decode QR code image files.
//	4. QRCodeVideoDecoder: Decode QR code using web camera.
//		This demo program is using some of the source modules of
//		Camera_Net project published at CodeProject.com:
//		https://www.codeproject.com/Articles/671407/Camera_Net-Library
//		and at GitHub: https://github.com/free5lot/Camera_Net.
//		This project is based on DirectShowLib.
//		http://sourceforge.net/projects/directshownet/
//		This project includes a modified subset of the source modules.
//
//	The main points of CPOL 1.02 subject to the terms of the License are:
//
//	Source Code and Executable Files can be used in commercial applications;
//	Source Code and Executable Files can be redistributed; and
//	Source Code can be modified to create derivative works.
//	No claim of suitability, guarantee, or any warranty whatsoever is
//	provided. The software is provided "as-is".
//	The Article accompanying the Work may not be distributed or republished
//	without the Author's consent
//
//	For version history please refer to QRDecoder.cs
/////////////////////////////////////////////////////////////////////

using System;

namespace QrCodeDecoderImageSharpUpgraded;

/// <summary>
/// QR code finder class
/// </summary>
internal sealed class Finder
{
    // horizontal scan
    internal int Row;
    internal int Col1;
    internal int Col2;
    internal double HModule;

    // vertical scan
    internal int Col;
    internal int Row1;
    internal int Row2;
    internal double VModule;

    internal double Distance;
    internal double ModuleSize;

    /// <summary>
    /// Constructor during horizontal scan
    /// </summary>
    internal Finder
            (
            int Row,
            int Col1,
            int Col2,
            double HModule
            )
    {
        this.Row = Row;
        this.Col1 = Col1;
        this.Col2 = Col2;
        this.HModule = HModule;
        this.Distance = double.MaxValue;
        return;
    }

    /// <summary>
    /// Match during vertical scan
    /// </summary>
    internal void Match
            (
            int Col,
            int Row1,
            int Row2,
            double VModule
            )
    {
        // test if horizontal and vertical are not related
        if (Col < this.Col1 || Col >= this.Col2 || this.Row < Row1 || this.Row >= Row2)
            return;

        // Module sizes must be about the same
        if (Math.Min(this.HModule, VModule) < Math.Max(this.HModule, VModule) * QRDecoder.MODULE_SIZE_DEVIATION)
            return;

        // calculate distance
        double DeltaX = Col - 0.5 * (this.Col1 + this.Col2);
        double DeltaY = this.Row - 0.5 * (Row1 + Row2);
        double Delta = Math.Sqrt(DeltaX * DeltaX + DeltaY * DeltaY);

        // distance between two points must be less than 2 pixels
        if (Delta > QRDecoder.HOR_VERT_SCAN_MAX_DISTANCE)
            return;

        // new result is better than last result
        if (Delta < this.Distance)
        {
            this.Col = Col;
            this.Row1 = Row1;
            this.Row2 = Row2;
            this.VModule = VModule;
            this.ModuleSize = 0.5 * (this.HModule + VModule);
            this.Distance = Delta;
        }
        return;
    }

    /// <summary>
    /// Horizontal and vertical scans overlap
    /// </summary>
    internal bool Overlap
            (
            Finder Other
            )
    {
        return Other.Col1 < this.Col2 && Other.Col2 >= this.Col1 && Other.Row1 < this.Row2 && Other.Row2 >= this.Row1;
    }

    /// <summary>
    /// Finder to string
    /// </summary>
    public override string ToString()
    {
        if (this.Distance == double.MaxValue)
        {
            return string.Format("Finder: Row: {0}, Col1: {1}, Col2: {2}, HModule: {3:0.00}", this.Row, this.Col1, this.Col2, this.HModule);
        }

        return string.Format("Finder: Row: {0}, Col: {1}, Module: {2:0.00}, Distance: {3:0.00}", this.Row, this.Col, this.ModuleSize, this.Distance);
    }
}
