using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp.Processing;
using Stef.Validation;
using System;
using System.Collections.Generic;
using System.Text;

namespace QrCodeDecoderImageSharpUpgraded;

public sealed class QRDecoder : StaticTables

{
    /// <summary>
    /// Gets QR Code matrix version
    /// </summary>
    public int QRCodeVersion { get; private set; }

    /// <summary>
    /// Gets QR Code matrix dimension in bits
    /// </summary>
    public int QRCodeDimension { get; private set; }

    /// <summary>
    /// Gets QR Code error correction code (L, M, Q, H)
    /// </summary>
    public ErrorCorrection ErrorCorrection { get; private set; }

    /// <summary>
    /// Error correction percent (L, M, Q, H)
    /// </summary>
    public int[] ErrCorrPercent = new int[] { 7, 15, 25, 30 };

    /// <summary>
    /// Get mask code (0 to 7)
    /// </summary>
    public int MaskCode { get; internal set; }

    /// <summary>
    /// ECI Assignment Value
    /// </summary>
    public int ECIAssignValue { get; internal set; }

    internal int ImageWidth;
    internal int ImageHeight;
    internal bool[,] BlackWhiteImage;
    internal List<Finder> FinderList;
    internal List<Finder> AlignList;
    internal List<byte[]> DataArrayList;
    internal int MaxCodewords;
    internal int MaxDataCodewords;
    internal int MaxDataBits;
    internal int ErrCorrCodewords;
    internal int BlocksGroup1;
    internal int DataCodewordsGroup1;
    internal int BlocksGroup2;
    internal int DataCodewordsGroup2;

    internal byte[] CodewordsArray;
    internal int CodewordsPtr;
    internal uint BitBuffer;
    internal int BitBufferLen;
    internal byte[,] BaseMatrix;
    internal byte[,] MaskMatrix;

    internal bool Trans4Mode;

    // transformation cooefficients from QR modules to image pixels
    internal double Trans3a;
    internal double Trans3b;
    internal double Trans3c;
    internal double Trans3d;
    internal double Trans3e;
    internal double Trans3f;

    // transformation matrix based on three finders plus one more point
    internal double Trans4a;
    internal double Trans4b;
    internal double Trans4c;
    internal double Trans4d;
    internal double Trans4e;
    internal double Trans4f;
    internal double Trans4g;
    internal double Trans4h;

    internal const double SIGNATURE_MAX_DEVIATION = 0.25;
    internal const double HOR_VERT_SCAN_MAX_DISTANCE = 2.0;
    internal const double MODULE_SIZE_DEVIATION = 0.5; // 0.75;
    internal const double CORNER_SIDE_LENGTH_DEV = 0.8;
    internal const double CORNER_RIGHT_ANGLE_DEV = 0.25; // about Sin(4 deg)
    internal const double ALIGNMENT_SEARCH_AREA = 0.3;

    private readonly ILogger _logger;

    public QRDecoder(ILogger logger = null)
    {
        this._logger = logger;
    }

    /// <summary>
    /// QRCode image decoder
    /// </summary>
    /// <param name="inputImage">Input image</param>
    /// <returns>Output byte arrays</returns>
    public byte[][] ImageDecoder(SixLabors.ImageSharp.Image inputImage)
    {
        int Start;
        try
        {
            // empty data string output
            this.DataArrayList = new List<byte[]>();

            // save image dimension
            this.ImageWidth = inputImage.Width;
            this.ImageHeight = inputImage.Height;

            Start = Environment.TickCount;
            this._logger?.LogDebug("Convert image to black and white");

            // convert input image to black and white boolean image
            if (!this.ConvertImageToBlackAndWhite(inputImage.CloneAs<SixLabors.ImageSharp.PixelFormats.Rgb24>()))
            {
                return null;
            }

            this._logger?.LogDebug("Time: {0}", Environment.TickCount - Start);
            this._logger?.LogDebug("Finders search");

            // horizontal search for finders
            if (!this.HorizontalFindersSearch())
            {
                this._logger?.LogError("HorizontalFindersSearch");
                return null;
            }

            this._logger?.LogDebug("Horizontal Finders count: {0}", this.FinderList.Count);

            // vertical search for finders
            this.VerticalFindersSearch();

            int MatchedCount = 0;
            foreach (Finder HF in this.FinderList)
                if (HF.Distance != double.MaxValue)
                    MatchedCount++;
            this._logger?.LogDebug("Matched Finders count: {0}", MatchedCount);
            this._logger?.LogDebug("Remove all unused finders");

            // remove unused finders
            if (!this.RemoveUnusedFinders())
            {
                this._logger?.LogError("RemoveUnusedFinders");
                return null;
            }

            this._logger?.LogDebug("Time: {0}", Environment.TickCount - Start);
            foreach (Finder HF in this.FinderList)
            {
                this._logger?.LogDebug(HF.ToString());
            }

            this._logger?.LogDebug("Search for QR corners");
        }
        catch
        {
            this._logger?.LogDebug("QR Code decoding failed (no finders).");
            return null;
        }

        // look for all possible 3 finder patterns
        int Index1End = this.FinderList.Count - 2;
        int Index2End = this.FinderList.Count - 1;
        int Index3End = this.FinderList.Count;
        for (int Index1 = 0; Index1 < Index1End; Index1++)
            for (int Index2 = Index1 + 1; Index2 < Index2End; Index2++)
                for (int Index3 = Index2 + 1; Index3 < Index3End; Index3++)
                {
                    try
                    {
                        // find 3 finders arranged in L shape
                        var corner = Corner.CreateCorner(this.FinderList[Index1], this.FinderList[Index2], this.FinderList[Index3]);

                        // not a valid corner
                        if (corner == null)
                        {
                            continue;
                        }

                        this._logger?.LogDebug("Decode Corner: Top Left:    {0}", corner.TopLeftFinder.ToString());
                        this._logger?.LogDebug("Decode Corner: Top Right:   {0}", corner.TopRightFinder.ToString());
                        this._logger?.LogDebug("Decode Corner: Bottom Left: {0}", corner.BottomLeftFinder.ToString());

                        // get corner info (version, error code and mask)
                        // continue if failed
                        if (!this.GetQRCodeCornerInfo(corner))
                        {
                            continue;
                        }

                        this._logger?.LogDebug("Decode QR code using three finders");

                        // decode corner using three finders
                        // continue if successful
                        if (this.DecodeQRCodeCorner(corner))
                            continue;

                        // qr code version 1 has no alignment mark
                        // in other words decode failed 
                        if (this.QRCodeVersion == 1)
                            continue;

                        // find bottom right alignment mark
                        // continue if failed
                        if (!this.FindAlignmentMark(corner))
                            continue;

                        // decode using 4 points
                        foreach (Finder Align in this.AlignList)
                        {
                            this._logger?.LogDebug("Calculated alignment mark: Row {0}, Col {1}", Align.Row, Align.Col);

                            // calculate transformation based on 3 finders and bottom right alignment mark
                            this.SetTransMatrix(corner, Align.Row, Align.Col);

                            // decode corner using three finders and one alignment mark
                            if (this.DecodeQRCodeCorner(corner))
                                break;
                        }
                    }
                    catch (Exception Ex)
                    {
                        this._logger?.LogDebug("Decode corner failed. {0}", Ex.Message);
                        continue;
                    }
                }

        this._logger?.LogDebug("Time: {0}", Environment.TickCount - Start);

        // not found exit
        if (this.DataArrayList.Count == 0)
        {
            this._logger?.LogError("No QR Code found");
            return null;
        }

        return this.DataArrayList.ToArray();
    }

    ////////////////////////////////////////////////////////////////////
    // Convert image to black and white boolean matrix
    ////////////////////////////////////////////////////////////////////
    internal bool ConvertImageToBlackAndWhite(SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgb24> InputImage)
    {

        InputImage.Mutate(i => i.BlackWhite());

        this.BlackWhiteImage = new bool[this.ImageHeight, this.ImageWidth];
        for (int Row = 0; Row < this.ImageHeight; Row++)
        {
            InputImage.ProcessPixelRows(px =>
            {
                var span = px.GetRowSpan(Row);
                for (int Col = 0; Col < this.ImageWidth; Col++)
                {

                    this.BlackWhiteImage[Row, Col] = span[Col].R == 0;
                }
            });
        }


        return true;
    }

    ////////////////////////////////////////////////////////////////////
    // search row by row for finders blocks
    ////////////////////////////////////////////////////////////////////
    internal bool HorizontalFindersSearch()
    {
        // create empty finders list
        this.FinderList = new List<Finder>();

        // look for finder patterns
        int[] ColPos = new int[this.ImageWidth + 1];
        int PosPtr = 0;

        // scan one row at a time
        for (int Row = 0; Row < this.ImageHeight; Row++)
        {
            // look for first black pixel
            int Col;
            for (Col = 0; Col < this.ImageWidth && !this.BlackWhiteImage[Row, Col]; Col++)
                ;
            if (Col == this.ImageWidth)
                continue;

            // first black
            PosPtr = 0;
            ColPos[PosPtr++] = Col;

            // loop for pairs
            for (; ; )
            {
                // look for next white
                // if black is all the way to the edge, set next white after the edge
                for (; Col < this.ImageWidth && this.BlackWhiteImage[Row, Col]; Col++)
                    ;
                ColPos[PosPtr++] = Col;
                if (Col == this.ImageWidth)
                    break;

                // look for next black
                for (; Col < this.ImageWidth && !this.BlackWhiteImage[Row, Col]; Col++)
                    ;
                if (Col == this.ImageWidth)
                    break;
                ColPos[PosPtr++] = Col;
            }

            // we must have at least 6 positions
            if (PosPtr < 6)
                continue;

            // build length array
            int PosLen = PosPtr - 1;
            int[] Len = new int[PosLen];
            for (int Ptr = 0; Ptr < PosLen; Ptr++)
                Len[Ptr] = ColPos[Ptr + 1] - ColPos[Ptr];

            // test signature
            int SigLen = PosPtr - 5;
            for (int SigPtr = 0; SigPtr < SigLen; SigPtr += 2)
            {
                if (this.TestFinderSig(ColPos, Len, SigPtr, out double ModuleSize))
                    this.FinderList.Add(new Finder(Row, ColPos[SigPtr + 2], ColPos[SigPtr + 3], ModuleSize));
            }
        }

        // no finders found
        if (this.FinderList.Count < 3)
        {
            this._logger?.LogDebug("Horizontal finders search. Less than 3 finders found");
            return false;
        }

        return true;
    }

    ////////////////////////////////////////////////////////////////////
    // search row by row for alignment blocks
    ////////////////////////////////////////////////////////////////////
    internal bool HorizontalAlignmentSearch
            (
            int AreaLeft,
            int AreaTop,
            int AreaWidth,
            int AreaHeight
            )
    {
        // create empty finders list
        this.AlignList = new List<Finder>();

        // look for finder patterns
        int[] ColPos = new int[AreaWidth + 1];
        int PosPtr = 0;

        // area right and bottom
        int AreaRight = AreaLeft + AreaWidth;
        int AreaBottom = AreaTop + AreaHeight;

        // scan one row at a time
        for (int Row = AreaTop; Row < AreaBottom; Row++)
        {
            // look for first black pixel
            int Col;
            for (Col = AreaLeft; Col < AreaRight && !this.BlackWhiteImage[Row, Col]; Col++)
                ;
            if (Col == AreaRight)
                continue;

            // first black
            PosPtr = 0;
            ColPos[PosPtr++] = Col;

            // loop for pairs
            for (; ; )
            {
                // look for next white
                // if black is all the way to the edge, set next white after the edge
                for (; Col < AreaRight && this.BlackWhiteImage[Row, Col]; Col++)
                    ;
                ColPos[PosPtr++] = Col;
                if (Col == AreaRight)
                    break;

                // look for next black
                for (; Col < AreaRight && !this.BlackWhiteImage[Row, Col]; Col++)
                    ;
                if (Col == AreaRight)
                    break;
                ColPos[PosPtr++] = Col;
            }

            // we must have at least 6 positions
            if (PosPtr < 6)
                continue;

            // build length array
            int PosLen = PosPtr - 1;
            int[] Len = new int[PosLen];
            for (int Ptr = 0; Ptr < PosLen; Ptr++)
                Len[Ptr] = ColPos[Ptr + 1] - ColPos[Ptr];

            // test signature
            int SigLen = PosPtr - 5;
            for (int SigPtr = 0; SigPtr < SigLen; SigPtr += 2)
            {
                if (this.TestAlignSig(ColPos, Len, SigPtr, out double ModuleSize))
                    this.AlignList.Add(new Finder(Row, ColPos[SigPtr + 2], ColPos[SigPtr + 3], ModuleSize));
            }
        }

        // list is now empty or has less than three finders
        if (this.AlignList.Count == 0)
            this._logger?.LogDebug("Vertical align search.\r\nNo finders found");

        // exit
        return this.AlignList.Count != 0;
    }

    ////////////////////////////////////////////////////////////////////
    // search column by column for finders blocks
    ////////////////////////////////////////////////////////////////////
    internal void VerticalFindersSearch()
    {
        // active columns
        bool[] ActiveColumn = new bool[this.ImageWidth];
        foreach (Finder HF in this.FinderList)
        {
            for (int Col = HF.Col1; Col < HF.Col2; Col++)
                ActiveColumn[Col] = true;
        }

        // look for finder patterns
        int[] RowPos = new int[this.ImageHeight + 1];
        int PosPtr = 0;

        // scan one column at a time
        for (int Col = 0; Col < this.ImageWidth; Col++)
        {
            // not active column
            if (!ActiveColumn[Col])
                continue;

            // look for first black pixel
            int Row;
            for (Row = 0; Row < this.ImageHeight && !this.BlackWhiteImage[Row, Col]; Row++)
                ;
            if (Row == this.ImageWidth)
                continue;

            // first black
            PosPtr = 0;
            RowPos[PosPtr++] = Row;

            // loop for pairs
            for (; ; )
            {
                // look for next white
                // if black is all the way to the edge, set next white after the edge
                for (; Row < this.ImageHeight && this.BlackWhiteImage[Row, Col]; Row++)
                    ;
                RowPos[PosPtr++] = Row;
                if (Row == this.ImageHeight)
                    break;

                // look for next black
                for (; Row < this.ImageHeight && !this.BlackWhiteImage[Row, Col]; Row++)
                    ;
                if (Row == this.ImageHeight)
                    break;
                RowPos[PosPtr++] = Row;
            }

            // we must have at least 6 positions
            if (PosPtr < 6)
                continue;

            // build length array
            int PosLen = PosPtr - 1;
            int[] Len = new int[PosLen];
            for (int Ptr = 0; Ptr < PosLen; Ptr++)
                Len[Ptr] = RowPos[Ptr + 1] - RowPos[Ptr];

            // test signature
            int SigLen = PosPtr - 5;
            for (int SigPtr = 0; SigPtr < SigLen; SigPtr += 2)
            {
                if (!this.TestFinderSig(RowPos, Len, SigPtr, out double ModuleSize))
                    continue;
                foreach (Finder HF in this.FinderList)
                {
                    HF.Match(Col, RowPos[SigPtr + 2], RowPos[SigPtr + 3], ModuleSize);
                }
            }
        }
    }

    ////////////////////////////////////////////////////////////////////
    // search column by column for finders blocks
    ////////////////////////////////////////////////////////////////////

    internal void VerticalAlignmentSearch
            (
            int AreaLeft,
            int AreaTop,
            int AreaWidth,
            int AreaHeight
            )
    {
        // active columns
        bool[] ActiveColumn = new bool[AreaWidth];
        foreach (Finder HF in this.AlignList)
        {
            for (int Col = HF.Col1; Col < HF.Col2; Col++)
                ActiveColumn[Col - AreaLeft] = true;
        }

        // look for finder patterns
        int[] RowPos = new int[AreaHeight + 1];
        int PosPtr = 0;

        // area right and bottom
        int AreaRight = AreaLeft + AreaWidth;
        int AreaBottom = AreaTop + AreaHeight;

        // scan one column at a time
        for (int Col = AreaLeft; Col < AreaRight; Col++)
        {
            // not active column
            if (!ActiveColumn[Col - AreaLeft])
                continue;

            // look for first black pixel
            int Row;
            for (Row = AreaTop; Row < AreaBottom && !this.BlackWhiteImage[Row, Col]; Row++)
                ;
            if (Row == AreaBottom)
                continue;

            // first black
            PosPtr = 0;
            RowPos[PosPtr++] = Row;

            // loop for pairs
            for (; ; )
            {
                // look for next white
                // if black is all the way to the edge, set next white after the edge
                for (; Row < AreaBottom && this.BlackWhiteImage[Row, Col]; Row++)
                    ;
                RowPos[PosPtr++] = Row;
                if (Row == AreaBottom)
                    break;

                // look for next black
                for (; Row < AreaBottom && !this.BlackWhiteImage[Row, Col]; Row++)
                    ;
                if (Row == AreaBottom)
                    break;
                RowPos[PosPtr++] = Row;
            }

            // we must have at least 6 positions
            if (PosPtr < 6)
                continue;

            // build length array
            int PosLen = PosPtr - 1;
            int[] Len = new int[PosLen];
            for (int Ptr = 0; Ptr < PosLen; Ptr++)
                Len[Ptr] = RowPos[Ptr + 1] - RowPos[Ptr];

            // test signature
            int SigLen = PosPtr - 5;
            for (int SigPtr = 0; SigPtr < SigLen; SigPtr += 2)
            {
                if (!this.TestAlignSig(RowPos, Len, SigPtr, out double ModuleSize))
                    continue;
                foreach (Finder HF in this.AlignList)
                {
                    HF.Match(Col, RowPos[SigPtr + 2], RowPos[SigPtr + 3], ModuleSize);
                }
            }
        }
    }

    ////////////////////////////////////////////////////////////////////
    // search column by column for finders blocks
    ////////////////////////////////////////////////////////////////////
    internal bool RemoveUnusedFinders()
    {
        // remove all entries without a match
        for (int Index = 0; Index < this.FinderList.Count; Index++)
        {
            if (this.FinderList[Index].Distance == double.MaxValue)
            {
                this.FinderList.RemoveAt(Index);
                Index--;
            }
        }

        // list is now empty or has less than three finders
        if (this.FinderList.Count < 3)
        {

            this._logger?.LogDebug("Remove unmatched finders. Less than 3 finders found");

            return false;
        }

        // keep best entry for each overlapping area
        for (int Index = 0; Index < this.FinderList.Count; Index++)
        {
            Finder Finder = this.FinderList[Index];
            for (int Index1 = Index + 1; Index1 < this.FinderList.Count; Index1++)
            {
                Finder Finder1 = this.FinderList[Index1];
                if (!Finder.Overlap(Finder1))
                    continue;
                if (Finder1.Distance < Finder.Distance)
                {
                    Finder = Finder1;
                    this.FinderList[Index] = Finder;
                }
                this.FinderList.RemoveAt(Index1);
                Index1--;
            }
        }

        // list is now empty or has less than three finders
        if (this.FinderList.Count < 3)
        {

            this._logger?.LogDebug("Keep best matched finders. Less than 3 finders found");

            return false;
        }

        // exit
        return true;
    }

    ////////////////////////////////////////////////////////////////////
    // search column by column for finders blocks
    ////////////////////////////////////////////////////////////////////

    internal bool RemoveUnusedAlignMarks()
    {
        // remove all entries without a match
        for (int Index = 0; Index < this.AlignList.Count; Index++)
        {
            if (this.AlignList[Index].Distance == double.MaxValue)
            {
                this.AlignList.RemoveAt(Index);
                Index--;
            }
        }

        // keep best entry for each overlapping area
        for (int Index = 0; Index < this.AlignList.Count; Index++)
        {
            Finder Finder = this.AlignList[Index];
            for (int Index1 = Index + 1; Index1 < this.AlignList.Count; Index1++)
            {
                Finder Finder1 = this.AlignList[Index1];
                if (!Finder.Overlap(Finder1))
                    continue;
                if (Finder1.Distance < Finder.Distance)
                {
                    Finder = Finder1;
                    this.AlignList[Index] = Finder;
                }
                this.AlignList.RemoveAt(Index1);
                Index1--;
            }
        }

        // list is now empty or has less than three finders

        if (this.AlignList.Count == 0)
            this._logger?.LogDebug("Remove unused alignment marks.\r\nNo alignment marks found");


        // exit
        return this.AlignList.Count != 0;
    }

    ////////////////////////////////////////////////////////////////////
    // test finder signature 1 1 3 1 1
    ////////////////////////////////////////////////////////////////////
    internal bool TestFinderSig(int[] Pos, int[] Len, int Index, out double Module)
    {
        Module = (Pos[Index + 5] - Pos[Index]) / 7.0;
        double MaxDev = SIGNATURE_MAX_DEVIATION * Module;
        if (Math.Abs(Len[Index] - Module) > MaxDev)
            return false;
        if (Math.Abs(Len[Index + 1] - Module) > MaxDev)
            return false;
        if (Math.Abs(Len[Index + 2] - 3 * Module) > MaxDev)
            return false;
        if (Math.Abs(Len[Index + 3] - Module) > MaxDev)
            return false;
        if (Math.Abs(Len[Index + 4] - Module) > MaxDev)
            return false;
        return true;
    }

    ////////////////////////////////////////////////////////////////////
    // test alignment signature n 1 1 1 n
    ////////////////////////////////////////////////////////////////////
    internal bool TestAlignSig
            (
            int[] Pos,
            int[] Len,
            int Index,
            out double Module
            )
    {
        Module = (Pos[Index + 4] - Pos[Index + 1]) / 3.0;
        double MaxDev = SIGNATURE_MAX_DEVIATION * Module;
        if (Len[Index] < Module - MaxDev)
            return false;
        if (Math.Abs(Len[Index + 1] - Module) > MaxDev)
            return false;
        if (Math.Abs(Len[Index + 2] - Module) > MaxDev)
            return false;
        if (Math.Abs(Len[Index + 3] - Module) > MaxDev)
            return false;
        if (Len[Index + 4] < Module - MaxDev)
            return false;
        return true;
    }

    ////////////////////////////////////////////////////////////////////
    // Build corner list
    ////////////////////////////////////////////////////////////////////

    internal List<Corner> BuildCornerList()
    {
        // empty list
        List<Corner> Corners = new List<Corner>();

        // look for all possible 3 finder patterns
        int Index1End = this.FinderList.Count - 2;
        int Index2End = this.FinderList.Count - 1;
        int Index3End = this.FinderList.Count;
        for (int Index1 = 0; Index1 < Index1End; Index1++)
            for (int Index2 = Index1 + 1; Index2 < Index2End; Index2++)
                for (int Index3 = Index2 + 1; Index3 < Index3End; Index3++)
                {
                    // find 3 finders arranged in L shape
                    Corner Corner = Corner.CreateCorner(this.FinderList[Index1], this.FinderList[Index2], this.FinderList[Index3]);

                    // add corner to list
                    if (Corner != null)
                        Corners.Add(Corner);
                }

        return Corners.Count == 0 ? null : Corners;
    }

    ////////////////////////////////////////////////////////////////////
    // Get QR Code corner info
    ////////////////////////////////////////////////////////////////////

    internal bool GetQRCodeCornerInfo(Corner corner)
    {
        try
        {
            // initial version number
            this.QRCodeVersion = corner.InitialVersionNumber();

            // qr code dimension
            this.QRCodeDimension = 17 + 4 * this.QRCodeVersion;


            this._logger?.LogDebug("Initial version number: {0}, dimension: {1}", this.QRCodeVersion, this.QRCodeDimension);


            // set transformation matrix
            this.SetTransMatrix(corner);

            // if version number is 7 or more, get version code
            if (this.QRCodeVersion >= 7)
            {
                int Version = this.GetVersionOne();
                if (Version == 0)
                {
                    Version = this.GetVersionTwo();
                    if (Version == 0)
                        return false;
                }

                // QR Code version number is different than initial version
                if (Version != this.QRCodeVersion)
                {
                    // initial version number and dimension
                    this.QRCodeVersion = Version;

                    // qr code dimension
                    this.QRCodeDimension = 17 + 4 * this.QRCodeVersion;


                    this._logger?.LogDebug("Updated version number: {0}, dimension: {1}", this.QRCodeVersion, this.QRCodeDimension);


                    // set transformation matrix
                    this.SetTransMatrix(corner);
                }
            }

            // get format info arrays
            int FormatInfo = this.GetFormatInfoOne();
            if (FormatInfo < 0)
            {
                FormatInfo = this.GetFormatInfoTwo();
                if (FormatInfo < 0)
                    return false;
            }

            // set error correction code and mask code
            this.ErrorCorrection = this.FormatInfoToErrCode(FormatInfo >> 3);
            this.MaskCode = FormatInfo & 7;

            // successful exit
            return true;
        }
        catch
        {
            this._logger?.LogDebug("Get QR Code corner info.");
            return false;
        }
    }

    ////////////////////////////////////////////////////////////////////
    // Search for QR Code version
    ////////////////////////////////////////////////////////////////////
    private bool DecodeQRCodeCorner(Corner corner)
    {
        try
        {
            // create base matrix
            this.BuildBaseMatrix();

            // create data matrix and test fixed modules
            this.ConvertImageToMatrix();

            // based on version and format information
            // set number of data and error correction codewords length  
            this.SetDataCodewordsLength();

            // apply mask as per get format information step
            this.ApplyMask(this.MaskCode);

            // unload data from binary matrix to byte format
            this.UnloadDataFromMatrix();

            // restore blocks (undo interleave)
            this.RestoreBlocks();

            // calculate error correction
            // in case of error try to correct it
            this.CalculateErrorCorrection();

            // decode data
            byte[] DataArray = this.DecodeData();
            this.DataArrayList.Add(DataArray);

            // trace
            this._logger?.LogDebug("Version: {0}, Dim: {1}, ErrCorr: {2}, Generator: {3}, Mask: {4}, Group1: {5}:{6}, Group2: {7}:{8}",
                this.QRCodeVersion.ToString(), this.QRCodeDimension.ToString(), this.ErrorCorrection.ToString(), this.ErrCorrCodewords.ToString(), this.MaskCode.ToString(),
                this.BlocksGroup1.ToString(), this.DataCodewordsGroup1.ToString(), this.BlocksGroup2.ToString(), this.DataCodewordsGroup2.ToString());
            this._logger?.LogDebug("Data: {0}", Convert.ToBase64String(DataArray));

            // successful exit
            return true;
        }
        catch
        {
            this._logger?.LogDebug("Decode QR code exception.");
            return false;
        }
    }

    internal void SetTransMatrix(Corner corner)
    {
        // save
        int bottomRightPos = this.QRCodeDimension - 4;

        // transformation matrix based on three finders
        double[,] Matrix1 = new double[3, 4];
        double[,] Matrix2 = new double[3, 4];

        // build matrix 1 for horizontal X direction
        Matrix1[0, 0] = 3;
        Matrix1[0, 1] = 3;
        Matrix1[0, 2] = 1;
        Matrix1[0, 3] = corner.TopLeftFinder.Col;

        Matrix1[1, 0] = bottomRightPos;
        Matrix1[1, 1] = 3;
        Matrix1[1, 2] = 1;
        Matrix1[1, 3] = corner.TopRightFinder.Col;

        Matrix1[2, 0] = 3;
        Matrix1[2, 1] = bottomRightPos;
        Matrix1[2, 2] = 1;
        Matrix1[2, 3] = corner.BottomLeftFinder.Col;

        // build matrix 2 for Vertical Y direction
        Matrix2[0, 0] = 3;
        Matrix2[0, 1] = 3;
        Matrix2[0, 2] = 1;
        Matrix2[0, 3] = corner.TopLeftFinder.Row;

        Matrix2[1, 0] = bottomRightPos;
        Matrix2[1, 1] = 3;
        Matrix2[1, 2] = 1;
        Matrix2[1, 3] = corner.TopRightFinder.Row;

        Matrix2[2, 0] = 3;
        Matrix2[2, 1] = bottomRightPos;
        Matrix2[2, 2] = 1;
        Matrix2[2, 3] = corner.BottomLeftFinder.Row;

        // solve matrix1
        this.SolveMatrixOne(Matrix1);
        this.Trans3a = Matrix1[0, 3];
        this.Trans3c = Matrix1[1, 3];
        this.Trans3e = Matrix1[2, 3];

        // solve matrix2
        this.SolveMatrixOne(Matrix2);
        this.Trans3b = Matrix2[0, 3];
        this.Trans3d = Matrix2[1, 3];
        this.Trans3f = Matrix2[2, 3];

        // reset trans 4 mode
        this.Trans4Mode = false;
        return;
    }

    internal void SolveMatrixOne(double[,] matrix)
    {
        for (int row = 0; row < 3; row++)
        {
            // If the element is zero, make it non zero by adding another row
            if (matrix[row, row] == 0)
            {
                int Row1;
                for (Row1 = row + 1; Row1 < 3 && matrix[Row1, row] == 0; Row1++)
                    ;

                if (Row1 == 3)
                {
                    throw new ApplicationException("Solve linear equations failed");
                }

                for (int Col = row; Col < 4; Col++)
                {
                    matrix[row, Col] += matrix[Row1, Col];
                }
            }

            // make the diagonal element 1.0
            for (int Col = 3; Col > row; Col--)
            {
                matrix[row, Col] /= matrix[row, row];
            }

            // subtract current row from next rows to eliminate one value
            for (int Row1 = row + 1; Row1 < 3; Row1++)
            {
                for (int Col = 3; Col > row; Col--)
                {
                    matrix[Row1, Col] -= matrix[row, Col] * matrix[Row1, row];
                }
            }
        }

        // go up from last row and eliminate all solved values
        matrix[1, 3] -= matrix[1, 2] * matrix[2, 3];
        matrix[0, 3] -= matrix[0, 2] * matrix[2, 3];
        matrix[0, 3] -= matrix[0, 1] * matrix[1, 3];
    }

    ////////////////////////////////////////////////////////////////////
    // Get image pixel color
    ////////////////////////////////////////////////////////////////////
    internal bool GetModule(int row, int col)
    {
        // get module based on three finders
        if (!this.Trans4Mode)
        {
            int Trans3Col = (int)Math.Round(this.Trans3a * col + this.Trans3c * row + this.Trans3e, 0, MidpointRounding.AwayFromZero);
            int Trans3Row = (int)Math.Round(this.Trans3b * col + this.Trans3d * row + this.Trans3f, 0, MidpointRounding.AwayFromZero);
            return this.BlackWhiteImage[Trans3Row, Trans3Col];
        }

        // get module based on three finders plus one alignment mark
        double W = this.Trans4g * col + this.Trans4h * row + 1.0;
        int Trans4Col = (int)Math.Round((this.Trans4a * col + this.Trans4b * row + this.Trans4c) / W, 0, MidpointRounding.AwayFromZero);
        int Trans4Row = (int)Math.Round((this.Trans4d * col + this.Trans4e * row + this.Trans4f) / W, 0, MidpointRounding.AwayFromZero);
        return this.BlackWhiteImage[Trans4Row, Trans4Col];
    }

    ////////////////////////////////////////////////////////////////////
    // search row by row for finders blocks
    ////////////////////////////////////////////////////////////////////
    private bool FindAlignmentMark(Corner corner)
    {
        // alignment mark estimated position
        int AlignRow = this.QRCodeDimension - 7;
        int AlignCol = this.QRCodeDimension - 7;
        int ImageCol = (int)Math.Round(this.Trans3a * AlignCol + this.Trans3c * AlignRow + this.Trans3e, 0, MidpointRounding.AwayFromZero);
        int ImageRow = (int)Math.Round(this.Trans3b * AlignCol + this.Trans3d * AlignRow + this.Trans3f, 0, MidpointRounding.AwayFromZero);

        this._logger?.LogDebug("Estimated alignment mark: Row {0}, Col {1}", ImageRow, ImageCol);

        // search area
        int Side = (int)Math.Round(ALIGNMENT_SEARCH_AREA * (corner.TopLineLength + corner.LeftLineLength), 0, MidpointRounding.AwayFromZero);

        int AreaLeft = ImageCol - Side / 2;
        int AreaTop = ImageRow - Side / 2;
        int AreaWidth = Side;
        int AreaHeight = Side;

        // horizontal search for finders
        if (!this.HorizontalAlignmentSearch(AreaLeft, AreaTop, AreaWidth, AreaHeight))
        {
            return false;
        }

        // vertical search for finders
        this.VerticalAlignmentSearch(AreaLeft, AreaTop, AreaWidth, AreaHeight);

        // remove unused alignment entries
        if (!this.RemoveUnusedAlignMarks())
        {
            return false;
        }

        return true;
    }

    internal void SetTransMatrix
            (
            Corner Corner,
            double ImageAlignRow,
            double ImageAlignCol
            )
    {
        // top right and bottom left QR code position
        int FarFinder = this.QRCodeDimension - 4;
        int FarAlign = this.QRCodeDimension - 7;

        double[,] Matrix = new double[8, 9];

        Matrix[0, 0] = 3.0;
        Matrix[0, 1] = 3.0;
        Matrix[0, 2] = 1.0;
        Matrix[0, 6] = -3.0 * Corner.TopLeftFinder.Col;
        Matrix[0, 7] = -3.0 * Corner.TopLeftFinder.Col;
        Matrix[0, 8] = Corner.TopLeftFinder.Col;

        Matrix[1, 0] = FarFinder;
        Matrix[1, 1] = 3.0;
        Matrix[1, 2] = 1.0;
        Matrix[1, 6] = -FarFinder * Corner.TopRightFinder.Col;
        Matrix[1, 7] = -3.0 * Corner.TopRightFinder.Col;
        Matrix[1, 8] = Corner.TopRightFinder.Col;

        Matrix[2, 0] = 3.0;
        Matrix[2, 1] = FarFinder;
        Matrix[2, 2] = 1.0;
        Matrix[2, 6] = -3.0 * Corner.BottomLeftFinder.Col;
        Matrix[2, 7] = -FarFinder * Corner.BottomLeftFinder.Col;
        Matrix[2, 8] = Corner.BottomLeftFinder.Col;

        Matrix[3, 0] = FarAlign;
        Matrix[3, 1] = FarAlign;
        Matrix[3, 2] = 1.0;
        Matrix[3, 6] = -FarAlign * ImageAlignCol;
        Matrix[3, 7] = -FarAlign * ImageAlignCol;
        Matrix[3, 8] = ImageAlignCol;

        Matrix[4, 3] = 3.0;
        Matrix[4, 4] = 3.0;
        Matrix[4, 5] = 1.0;
        Matrix[4, 6] = -3.0 * Corner.TopLeftFinder.Row;
        Matrix[4, 7] = -3.0 * Corner.TopLeftFinder.Row;
        Matrix[4, 8] = Corner.TopLeftFinder.Row;

        Matrix[5, 3] = FarFinder;
        Matrix[5, 4] = 3.0;
        Matrix[5, 5] = 1.0;
        Matrix[5, 6] = -FarFinder * Corner.TopRightFinder.Row;
        Matrix[5, 7] = -3.0 * Corner.TopRightFinder.Row;
        Matrix[5, 8] = Corner.TopRightFinder.Row;

        Matrix[6, 3] = 3.0;
        Matrix[6, 4] = FarFinder;
        Matrix[6, 5] = 1.0;
        Matrix[6, 6] = -3.0 * Corner.BottomLeftFinder.Row;
        Matrix[6, 7] = -FarFinder * Corner.BottomLeftFinder.Row;
        Matrix[6, 8] = Corner.BottomLeftFinder.Row;

        Matrix[7, 3] = FarAlign;
        Matrix[7, 4] = FarAlign;
        Matrix[7, 5] = 1.0;
        Matrix[7, 6] = -FarAlign * ImageAlignRow;
        Matrix[7, 7] = -FarAlign * ImageAlignRow;
        Matrix[7, 8] = ImageAlignRow;

        for (int Row = 0; Row < 8; Row++)
        {
            // If the element is zero, make it non zero by adding another row
            if (Matrix[Row, Row] == 0)
            {
                int Row1;
                for (Row1 = Row + 1; Row1 < 8 && Matrix[Row1, Row] == 0; Row1++)
                    ;
                if (Row1 == 8)
                    throw new ApplicationException("Solve linear equations failed");

                for (int Col = Row; Col < 9; Col++)
                    Matrix[Row, Col] += Matrix[Row1, Col];
            }

            // make the diagonal element 1.0
            for (int Col = 8; Col > Row; Col--)
                Matrix[Row, Col] /= Matrix[Row, Row];

            // subtract current row from next rows to eliminate one value
            for (int Row1 = Row + 1; Row1 < 8; Row1++)
            {
                for (int Col = 8; Col > Row; Col--)
                    Matrix[Row1, Col] -= Matrix[Row, Col] * Matrix[Row1, Row];
            }
        }

        // go up from last row and eliminate all solved values
        for (int Col = 7; Col > 0; Col--)
            for (int Row = Col - 1; Row >= 0; Row--)
            {
                Matrix[Row, 8] -= Matrix[Row, Col] * Matrix[Col, 8];
            }

        this.Trans4a = Matrix[0, 8];
        this.Trans4b = Matrix[1, 8];
        this.Trans4c = Matrix[2, 8];
        this.Trans4d = Matrix[3, 8];
        this.Trans4e = Matrix[4, 8];
        this.Trans4f = Matrix[5, 8];
        this.Trans4g = Matrix[6, 8];
        this.Trans4h = Matrix[7, 8];

        // set trans 4 mode
        this.Trans4Mode = true;
        return;
    }

    ////////////////////////////////////////////////////////////////////
    // Get version code bits top right
    ////////////////////////////////////////////////////////////////////

    internal int GetVersionOne()
    {
        int VersionCode = 0;
        for (int Index = 0; Index < 18; Index++)
        {
            if (this.GetModule(Index / 3, this.QRCodeDimension - 11 + (Index % 3)))
                VersionCode |= 1 << Index;
        }
        return this.TestVersionCode(VersionCode);
    }

    ////////////////////////////////////////////////////////////////////
    // Get version code bits bottom left
    ////////////////////////////////////////////////////////////////////

    internal int GetVersionTwo()
    {
        int VersionCode = 0;
        for (int Index = 0; Index < 18; Index++)
        {
            if (this.GetModule(this.QRCodeDimension - 11 + (Index % 3), Index / 3))
                VersionCode |= 1 << Index;
        }
        return this.TestVersionCode(VersionCode);
    }

    ////////////////////////////////////////////////////////////////////
    // Test version code bits
    ////////////////////////////////////////////////////////////////////

    internal int TestVersionCode
            (
            int VersionCode
            )
    {
        // format info
        int Code = VersionCode >> 12;

        // test for exact match
        if (Code >= 7 && Code <= 40 && VersionCodeArray[Code - 7] == VersionCode)
        {

            this._logger?.LogDebug("Version code exact match: {0:X4}, Version: {1}", VersionCode, Code);

            return Code;
        }

        // look for a match
        int BestInfo = 0;
        int Error = int.MaxValue;
        for (int Index = 0; Index < 34; Index++)
        {
            // test for exact match
            int ErrorBits = VersionCodeArray[Index] ^ VersionCode;
            if (ErrorBits == 0)
                return VersionCode >> 12;

            // count errors
            int ErrorCount = this.CountBits(ErrorBits);

            // save best result
            if (ErrorCount < Error)
            {
                Error = ErrorCount;
                BestInfo = Index;
            }
        }


        if (Error <= 3)
            this._logger?.LogDebug("Version code match with errors: {0:X4}, Version: {1}, Errors: {2}",
                VersionCode, BestInfo + 7, Error);
        else
            this._logger?.LogDebug("Version code no match: {0:X4}", VersionCode);


        return Error <= 3 ? BestInfo + 7 : 0;
    }

    ////////////////////////////////////////////////////////////////////
    // Get format info around top left corner
    ////////////////////////////////////////////////////////////////////

    public int GetFormatInfoOne()
    {
        int Info = 0;
        for (int Index = 0; Index < 15; Index++)
        {
            if (this.GetModule(FormatInfoOne[Index, 0], FormatInfoOne[Index, 1]))
                Info |= 1 << Index;
        }
        return this.TestFormatInfo(Info);
    }

    ////////////////////////////////////////////////////////////////////
    // Get format info around top right and bottom left corners
    ////////////////////////////////////////////////////////////////////

    internal int GetFormatInfoTwo()
    {
        int Info = 0;
        for (int Index = 0; Index < 15; Index++)
        {
            int Row = FormatInfoTwo[Index, 0];
            if (Row < 0)
                Row += this.QRCodeDimension;
            int Col = FormatInfoTwo[Index, 1];
            if (Col < 0)
                Col += this.QRCodeDimension;
            if (this.GetModule(Row, Col))
                Info |= 1 << Index;
        }
        return this.TestFormatInfo(Info);
    }

    ////////////////////////////////////////////////////////////////////
    // Test format info bits
    ////////////////////////////////////////////////////////////////////

    internal int TestFormatInfo
            (
            int FormatInfo
            )
    {
        // format info
        int Info = (FormatInfo ^ 0x5412) >> 10;

        // test for exact match
        if (FormatInfoArray[Info] == FormatInfo)
        {

            this._logger?.LogDebug("Format info exact match: {0:X4}, EC: {1}, mask: {2}",
                FormatInfo, this.FormatInfoToErrCode(Info >> 3).ToString(), Info & 7);

            return Info;
        }

        // look for a match
        int BestInfo = 0;
        int Error = int.MaxValue;
        for (int Index = 0; Index < 32; Index++)
        {
            int ErrorCount = this.CountBits(FormatInfoArray[Index] ^ FormatInfo);
            if (ErrorCount < Error)
            {
                Error = ErrorCount;
                BestInfo = Index;
            }
        }


        if (Error <= 3)
            this._logger?.LogDebug("Format info match with errors: {0:X4}, EC: {1}, mask: {2}, errors: {3}",
                FormatInfo, this.FormatInfoToErrCode(Info >> 3).ToString(), Info & 7, Error);
        else
            this._logger?.LogDebug("Format info no match: {0:X4}", FormatInfo);


        return Error <= 3 ? BestInfo : -1;
    }

    ////////////////////////////////////////////////////////////////////
    // Count Bits
    ////////////////////////////////////////////////////////////////////

    internal int CountBits
            (
            int Value
            )
    {
        int Count = 0;
        for (int Mask = 0x4000; Mask != 0; Mask >>= 1)
            if ((Value & Mask) != 0)
                Count++;
        return Count;
    }

    ////////////////////////////////////////////////////////////////////
    // Convert image to qr code matrix and test fixed modules
    ////////////////////////////////////////////////////////////////////

    internal void ConvertImageToMatrix()
    {
        // loop for all modules
        int FixedCount = 0;
        int ErrorCount = 0;
        for (int Row = 0; Row < this.QRCodeDimension; Row++)
            for (int Col = 0; Col < this.QRCodeDimension; Col++)
            {
                // the module (Row, Col) is not a fixed module 
                if ((this.BaseMatrix[Row, Col] & Fixed) == 0)
                {
                    if (this.GetModule(Row, Col))
                        this.BaseMatrix[Row, Col] |= Black;
                }

                // fixed module
                else
                {
                    // total fixed modules
                    FixedCount++;

                    // test for error
                    if ((this.GetModule(Row, Col) ? Black : White) != (this.BaseMatrix[Row, Col] & 1))
                        ErrorCount++;
                }
            }


        if (ErrorCount == 0)
        {
            this._logger?.LogDebug("Fixed modules no error");
        }
        else if (ErrorCount <= FixedCount * this.ErrCorrPercent[(int)this.ErrorCorrection] / 100)
        {
            this._logger?.LogDebug("Fixed modules some errors: {0} / {1}", ErrorCount, FixedCount);
        }
        else
        {
            this._logger?.LogDebug("Fixed modules too many errors: {0} / {1}", ErrorCount, FixedCount);
        }

        if (ErrorCount > FixedCount * this.ErrCorrPercent[(int)this.ErrorCorrection] / 100)
            throw new ApplicationException("Fixed modules error");
        return;
    }

    ////////////////////////////////////////////////////////////////////
    // Unload matrix data from base matrix
    ////////////////////////////////////////////////////////////////////

    internal void UnloadDataFromMatrix()
    {
        // input array pointer initialization
        int Ptr = 0;
        int PtrEnd = 8 * this.MaxCodewords;
        this.CodewordsArray = new byte[this.MaxCodewords];

        // bottom right corner of output matrix
        int Row = this.QRCodeDimension - 1;
        int Col = this.QRCodeDimension - 1;

        // step state
        int State = 0;
        for (; ; )
        {
            // current module is data
            if ((this.MaskMatrix[Row, Col] & NonData) == 0)
            {
                // unload current module with
                if ((this.MaskMatrix[Row, Col] & 1) != 0)
                    this.CodewordsArray[Ptr >> 3] |= (byte)(1 << (7 - (Ptr & 7)));
                if (++Ptr == PtrEnd)
                    break;
            }

            // current module is non data and vertical timing line condition is on
            else if (Col == 6)
                Col--;

            // update matrix position to next module
            switch (State)
            {
                // going up: step one to the left
                case 0:
                    Col--;
                    State = 1;
                    continue;

                // going up: step one row up and one column to the right
                case 1:
                    Col++;
                    Row--;
                    // we are not at the top, go to state 0
                    if (Row >= 0)
                    {
                        State = 0;
                        continue;
                    }
                    // we are at the top, step two columns to the left and start going down
                    Col -= 2;
                    Row = 0;
                    State = 2;
                    continue;

                // going down: step one to the left
                case 2:
                    Col--;
                    State = 3;
                    continue;

                // going down: step one row down and one column to the right
                case 3:
                    Col++;
                    Row++;
                    // we are not at the bottom, go to state 2
                    if (Row < this.QRCodeDimension)
                    {
                        State = 2;
                        continue;
                    }
                    // we are at the bottom, step two columns to the left and start going up
                    Col -= 2;
                    Row = this.QRCodeDimension - 1;
                    State = 0;
                    continue;
            }
        }
        return;
    }

    ////////////////////////////////////////////////////////////////////
    // Restore interleave data and error correction blocks
    ////////////////////////////////////////////////////////////////////

    internal void RestoreBlocks()
    {
        // allocate temp codewords array
        byte[] TempArray = new byte[this.MaxCodewords];

        // total blocks
        int TotalBlocks = this.BlocksGroup1 + this.BlocksGroup2;

        // create array of data blocks starting point
        int[] Start = new int[TotalBlocks];
        for (int Index = 1; Index < TotalBlocks; Index++)
            Start[Index] = Start[Index - 1] + (Index <= this.BlocksGroup1 ? this.DataCodewordsGroup1 : this.DataCodewordsGroup2);

        // step one. iterleave base on group one length
        int PtrEnd = this.DataCodewordsGroup1 * TotalBlocks;

        // restore group one and two
        int Ptr;
        int Block = 0;
        for (Ptr = 0; Ptr < PtrEnd; Ptr++)
        {
            TempArray[Start[Block]] = this.CodewordsArray[Ptr];
            Start[Block]++;
            Block++;
            if (Block == TotalBlocks)
                Block = 0;
        }

        // restore group two
        if (this.DataCodewordsGroup2 > this.DataCodewordsGroup1)
        {
            // step one. iterleave base on group one length
            PtrEnd = this.MaxDataCodewords;

            Block = this.BlocksGroup1;
            for (; Ptr < PtrEnd; Ptr++)
            {
                TempArray[Start[Block]] = this.CodewordsArray[Ptr];
                Start[Block]++;
                Block++;
                if (Block == TotalBlocks)
                    Block = this.BlocksGroup1;
            }
        }

        // create array of error correction blocks starting point
        Start[0] = this.MaxDataCodewords;
        for (int Index = 1; Index < TotalBlocks; Index++)
            Start[Index] = Start[Index - 1] + this.ErrCorrCodewords;

        // restore all groups
        PtrEnd = this.MaxCodewords;
        Block = 0;
        for (; Ptr < PtrEnd; Ptr++)
        {
            TempArray[Start[Block]] = this.CodewordsArray[Ptr];
            Start[Block]++;
            Block++;
            if (Block == TotalBlocks)
                Block = 0;
        }

        // save result
        this.CodewordsArray = TempArray;
        return;
    }

    ////////////////////////////////////////////////////////////////////
    // Calculate Error Correction
    ////////////////////////////////////////////////////////////////////

    protected void CalculateErrorCorrection()
    {
        // total error count
        int TotalErrorCount = 0;

        // set generator polynomial array
        byte[] Generator = GenArray[this.ErrCorrCodewords - 7];

        // error correcion calculation buffer
        int BufSize = Math.Max(this.DataCodewordsGroup1, this.DataCodewordsGroup2) + this.ErrCorrCodewords;
        byte[] ErrCorrBuff = new byte[BufSize];

        // initial number of data codewords
        int DataCodewords = this.DataCodewordsGroup1;
        int BuffLen = DataCodewords + this.ErrCorrCodewords;

        // codewords pointer
        int DataCodewordsPtr = 0;

        // codewords buffer error correction pointer
        int CodewordsArrayErrCorrPtr = this.MaxDataCodewords;

        // loop one block at a time
        int TotalBlocks = this.BlocksGroup1 + this.BlocksGroup2;
        for (int BlockNumber = 0; BlockNumber < TotalBlocks; BlockNumber++)
        {
            // switch to group2 data codewords
            if (BlockNumber == this.BlocksGroup1)
            {
                DataCodewords = this.DataCodewordsGroup2;
                BuffLen = DataCodewords + this.ErrCorrCodewords;
            }

            // copy next block of codewords to the buffer and clear the remaining part
            Array.Copy(this.CodewordsArray, DataCodewordsPtr, ErrCorrBuff, 0, DataCodewords);
            Array.Copy(this.CodewordsArray, CodewordsArrayErrCorrPtr, ErrCorrBuff, DataCodewords, this.ErrCorrCodewords);

            // make a duplicate
            byte[] CorrectionBuffer = (byte[])ErrCorrBuff.Clone();

            // error correction polynomial division
            ReedSolomon.PolynominalDivision(ErrCorrBuff, BuffLen, Generator, this.ErrCorrCodewords);

            // test for error
            int Index;
            for (Index = 0; Index < this.ErrCorrCodewords && ErrCorrBuff[DataCodewords + Index] == 0; Index++)
                ;

            if (Index < this.ErrCorrCodewords)
            {
                // correct the error
                int ErrorCount = ReedSolomon.CorrectData(CorrectionBuffer, BuffLen, this.ErrCorrCodewords);
                if (ErrorCount <= 0)
                {
                    throw new ApplicationException("Data is damaged. Error correction failed");
                }

                TotalErrorCount += ErrorCount;

                // fix the data
                Array.Copy(CorrectionBuffer, 0, this.CodewordsArray, DataCodewordsPtr, DataCodewords);
            }

            // update codewords array to next buffer
            DataCodewordsPtr += DataCodewords;

            // update pointer				
            CodewordsArrayErrCorrPtr += this.ErrCorrCodewords;
        }


        if (TotalErrorCount == 0)
        {
            this._logger?.LogDebug("No data errors");
        }
        else
        {
            this._logger?.LogDebug("Error correction applied to data. Total errors: " + TotalErrorCount.ToString());
        }

    }

    ////////////////////////////////////////////////////////////////////
    // Convert bit array to byte array
    ////////////////////////////////////////////////////////////////////
    internal byte[] DecodeData()
    {
        // bit buffer initial condition
        this.BitBuffer = (uint)((this.CodewordsArray[0] << 24) | (this.CodewordsArray[1] << 16) | (this.CodewordsArray[2] << 8) | this.CodewordsArray[3]);
        this.BitBufferLen = 32;
        this.CodewordsPtr = 4;

        // allocate data byte list
        List<byte> DataSeg = new List<byte>();

        // reset ECI assignment value
        this.ECIAssignValue = -1;

        // data might be made of blocks
        for (; ; )
        {
            // first 4 bits is mode indicator
            EncodingMode EncodingMode = (EncodingMode)this.ReadBitsFromCodewordsArray(4);

            // end of data
            if (EncodingMode <= 0)
                break;

            // test for encoding ECI assignment number
            if (EncodingMode == EncodingMode.ECI)
            {
                // one byte assinment value
                this.ECIAssignValue = this.ReadBitsFromCodewordsArray(8);
                if ((this.ECIAssignValue & 0x80) == 0)
                    continue;

                // two bytes assinment value
                this.ECIAssignValue = (this.ECIAssignValue << 8) | this.ReadBitsFromCodewordsArray(8);
                if ((this.ECIAssignValue & 0x4000) == 0)
                {
                    this.ECIAssignValue &= 0x3fff;
                    continue;
                }

                // three bytes assinment value
                this.ECIAssignValue = (this.ECIAssignValue << 8) | this.ReadBitsFromCodewordsArray(8);
                if ((this.ECIAssignValue & 0x200000) == 0)
                {
                    this.ECIAssignValue &= 0x1fffff;
                    continue;
                }
                throw new ApplicationException("ECI encoding assinment number in error");
            }

            // read data length
            int DataLength = this.ReadBitsFromCodewordsArray(this.DataLengthBits(EncodingMode));
            if (DataLength < 0)
            {
                throw new ApplicationException("Premature end of data (DataLengh)");
            }

            // save start of segment
            int SegStart = DataSeg.Count;

            // switch based on encode mode
            // numeric code indicator is 0001, alpha numeric 0010, byte 0100
            switch (EncodingMode)
            {
                // numeric mode
                case EncodingMode.Numeric:
                    // encode digits in groups of 2
                    int NumericEnd = (DataLength / 3) * 3;
                    for (int Index = 0; Index < NumericEnd; Index += 3)
                    {
                        int Temp = this.ReadBitsFromCodewordsArray(10);
                        if (Temp < 0)
                        {
                            throw new ApplicationException("Premature end of data (Numeric 1)");
                        }
                        DataSeg.Add(DecodingTable[Temp / 100]);
                        DataSeg.Add(DecodingTable[(Temp % 100) / 10]);
                        DataSeg.Add(DecodingTable[Temp % 10]);
                    }

                    // we have one character remaining
                    if (DataLength - NumericEnd == 1)
                    {
                        int Temp = this.ReadBitsFromCodewordsArray(4);
                        if (Temp < 0)
                        {
                            throw new ApplicationException("Premature end of data (Numeric 2)");
                        }
                        DataSeg.Add(DecodingTable[Temp]);
                    }

                    // we have two character remaining
                    else if (DataLength - NumericEnd == 2)
                    {
                        int Temp = this.ReadBitsFromCodewordsArray(7);
                        if (Temp < 0)
                        {
                            throw new ApplicationException("Premature end of data (Numeric 3)");
                        }
                        DataSeg.Add(DecodingTable[Temp / 10]);
                        DataSeg.Add(DecodingTable[Temp % 10]);
                    }
                    break;

                // alphanumeric mode
                case EncodingMode.AlphaNumeric:
                    // encode digits in groups of 2
                    int AlphaNumEnd = (DataLength / 2) * 2;
                    for (int Index = 0; Index < AlphaNumEnd; Index += 2)
                    {
                        int Temp = this.ReadBitsFromCodewordsArray(11);
                        if (Temp < 0)
                        {
                            throw new ApplicationException("Premature end of data (Alpha Numeric 1)");
                        }
                        DataSeg.Add(DecodingTable[Temp / 45]);
                        DataSeg.Add(DecodingTable[Temp % 45]);
                    }

                    // we have one character remaining
                    if (DataLength - AlphaNumEnd == 1)
                    {
                        int Temp = this.ReadBitsFromCodewordsArray(6);
                        if (Temp < 0)
                        {
                            throw new ApplicationException("Premature end of data (Alpha Numeric 2)");
                        }
                        DataSeg.Add(DecodingTable[Temp]);
                    }
                    break;

                // byte mode					
                case EncodingMode.Byte:
                    // append the data after mode and character count
                    for (int Index = 0; Index < DataLength; Index++)
                    {
                        int Temp = this.ReadBitsFromCodewordsArray(8);
                        if (Temp < 0)
                        {
                            throw new ApplicationException("Premature end of data (byte mode)");
                        }
                        DataSeg.Add((byte)Temp);
                    }
                    break;

                default:
                    throw new ApplicationException(string.Format("Encoding mode not supported {0}", EncodingMode.ToString()));
            }

            if (DataLength != DataSeg.Count - SegStart)
                throw new ApplicationException("Data encoding length in error");
        }

        // save data
        return DataSeg.ToArray();
    }

    ////////////////////////////////////////////////////////////////////
    // Read data from codeword array
    ////////////////////////////////////////////////////////////////////

    internal int ReadBitsFromCodewordsArray(int bits)
    {
        if (bits > this.BitBufferLen)
        {
            return -1;
        }

        int data = (int)(this.BitBuffer >> (32 - bits));
        this.BitBuffer <<= bits;
        this.BitBufferLen -= bits;
        while (this.BitBufferLen <= 24 && this.CodewordsPtr < this.MaxDataCodewords)
        {
            this.BitBuffer |= (uint)(this.CodewordsArray[this.CodewordsPtr++] << (24 - this.BitBufferLen));
            this.BitBufferLen += 8;
        }

        return data;
    }

    ////////////////////////////////////////////////////////////////////
    // Set encoded data bits length
    ////////////////////////////////////////////////////////////////////
    private int DataLengthBits(EncodingMode encodingMode)
    {
        // Data length bits
        switch (encodingMode)
        {
            // numeric mode
            case EncodingMode.Numeric:
                return this.QRCodeVersion < 10 ? 10 : (this.QRCodeVersion < 27 ? 12 : 14);

            // alpha numeric mode
            case EncodingMode.AlphaNumeric:
                return this.QRCodeVersion < 10 ? 9 : (this.QRCodeVersion < 27 ? 11 : 13);

            // byte mode
            case EncodingMode.Byte:
                return this.QRCodeVersion < 10 ? 8 : 16;
        }

        throw new ApplicationException("Unsupported encoding mode " + encodingMode.ToString());
    }

    ////////////////////////////////////////////////////////////////////
    // Set data and error correction codewords length
    ////////////////////////////////////////////////////////////////////
    internal void SetDataCodewordsLength()
    {
        // index shortcut
        int BlockInfoIndex = (this.QRCodeVersion - 1) * 4 + (int)this.ErrorCorrection;

        // Number of blocks in group 1
        this.BlocksGroup1 = ECBlockInfo[BlockInfoIndex, BLOCKS_GROUP1];

        // Number of data codewords in blocks of group 1
        this.DataCodewordsGroup1 = ECBlockInfo[BlockInfoIndex, DATA_CODEWORDS_GROUP1];

        // Number of blocks in group 2
        this.BlocksGroup2 = ECBlockInfo[BlockInfoIndex, BLOCKS_GROUP2];

        // Number of data codewords in blocks of group 2
        this.DataCodewordsGroup2 = ECBlockInfo[BlockInfoIndex, DATA_CODEWORDS_GROUP2];

        // Total number of data codewords for this version and EC level
        this.MaxDataCodewords = this.BlocksGroup1 * this.DataCodewordsGroup1 + this.BlocksGroup2 * this.DataCodewordsGroup2;
        this.MaxDataBits = 8 * this.MaxDataCodewords;

        // total data plus error correction bits
        this.MaxCodewords = MaxCodewordsArray[this.QRCodeVersion];

        // Error correction codewords per block
        this.ErrCorrCodewords = (this.MaxCodewords - this.MaxDataCodewords) / (this.BlocksGroup1 + this.BlocksGroup2);
    }

    ////////////////////////////////////////////////////////////////////
    // Format info to error correction code
    ////////////////////////////////////////////////////////////////////

    internal ErrorCorrection FormatInfoToErrCode(int info)
    {
        return (ErrorCorrection)(info ^ 1);
    }

    ////////////////////////////////////////////////////////////////////
    // Build Base Matrix
    ////////////////////////////////////////////////////////////////////
    internal void BuildBaseMatrix()
    {
        // allocate base matrix
        this.BaseMatrix = new byte[this.QRCodeDimension + 5, this.QRCodeDimension + 5];

        // top left finder patterns
        for (int Row = 0; Row < 9; Row++)
            for (int Col = 0; Col < 9; Col++)
                this.BaseMatrix[Row, Col] = FinderPatternTopLeft[Row, Col];

        // top right finder patterns
        int Pos = this.QRCodeDimension - 8;
        for (int Row = 0; Row < 9; Row++)
            for (int Col = 0; Col < 8; Col++)
                this.BaseMatrix[Row, Pos + Col] = FinderPatternTopRight[Row, Col];

        // bottom left finder patterns
        for (int Row = 0; Row < 8; Row++)
            for (int Col = 0; Col < 9; Col++)
                this.BaseMatrix[Pos + Row, Col] = FinderPatternBottomLeft[Row, Col];

        // Timing pattern
        for (int Z = 8; Z < this.QRCodeDimension - 8; Z++)
            this.BaseMatrix[Z, 6] = this.BaseMatrix[6, Z] = (Z & 1) == 0 ? FixedBlack : FixedWhite;

        // alignment pattern
        if (this.QRCodeVersion > 1)
        {
            byte[] AlignPos = AlignmentPositionArray[this.QRCodeVersion];
            int AlignmentDimension = AlignPos.Length;
            for (int Row = 0; Row < AlignmentDimension; Row++)
                for (int Col = 0; Col < AlignmentDimension; Col++)
                {
                    if (Col == 0 && Row == 0 || Col == AlignmentDimension - 1 && Row == 0 || Col == 0 && Row == AlignmentDimension - 1)
                    {
                        continue;
                    }

                    int PosRow = AlignPos[Row];
                    int PosCol = AlignPos[Col];
                    for (int ARow = -2; ARow < 3; ARow++)
                        for (int ACol = -2; ACol < 3; ACol++)
                        {
                            this.BaseMatrix[PosRow + ARow, PosCol + ACol] = AlignmentPattern[ARow + 2, ACol + 2];
                        }
                }
        }

        // reserve version information
        if (this.QRCodeVersion >= 7)
        {
            // position of 3 by 6 rectangles
            Pos = this.QRCodeDimension - 11;

            // top right
            for (int Row = 0; Row < 6; Row++)
                for (int Col = 0; Col < 3; Col++)
                {
                    this.BaseMatrix[Row, Pos + Col] = FormatWhite;
                }

            // bottom right
            for (int Col = 0; Col < 6; Col++)
                for (int Row = 0; Row < 3; Row++)
                {
                    this.BaseMatrix[Pos + Row, Col] = FormatWhite;
                }
        }
    }

    ////////////////////////////////////////////////////////////////////
    // Apply Mask
    ////////////////////////////////////////////////////////////////////
    internal void ApplyMask(int mask)
    {
        this.MaskMatrix = (byte[,])this.BaseMatrix.Clone();
        switch (mask)
        {
            case 0:
                this.ApplyMask0();
                break;

            case 1:
                this.ApplyMask1();
                break;

            case 2:
                this.ApplyMask2();
                break;

            case 3:
                this.ApplyMask3();
                break;

            case 4:
                this.ApplyMask4();
                break;

            case 5:
                this.ApplyMask5();
                break;

            case 6:
                this.ApplyMask6();
                break;

            case 7:
                this.ApplyMask7();
                break;
        }
    }

    ////////////////////////////////////////////////////////////////////
    // Apply Mask 0
    // (row + column) % 2 == 0
    ////////////////////////////////////////////////////////////////////
    internal void ApplyMask0()
    {
        for (int Row = 0; Row < this.QRCodeDimension; Row += 2)
            for (int Col = 0; Col < this.QRCodeDimension; Col += 2)
            {
                if ((this.MaskMatrix[Row, Col] & NonData) == 0)
                    this.MaskMatrix[Row, Col] ^= 1;
                if ((this.MaskMatrix[Row + 1, Col + 1] & NonData) == 0)
                    this.MaskMatrix[Row + 1, Col + 1] ^= 1;
            }
    }

    ////////////////////////////////////////////////////////////////////
    // Apply Mask 1
    // row % 2 == 0
    ////////////////////////////////////////////////////////////////////
    internal void ApplyMask1()
    {
        for (int Row = 0; Row < this.QRCodeDimension; Row += 2)
            for (int Col = 0; Col < this.QRCodeDimension; Col++)
                if ((this.MaskMatrix[Row, Col] & NonData) == 0)
                    this.MaskMatrix[Row, Col] ^= 1;
    }

    ////////////////////////////////////////////////////////////////////
    // Apply Mask 2
    // column % 3 == 0
    ////////////////////////////////////////////////////////////////////
    internal void ApplyMask2()
    {
        for (int Row = 0; Row < this.QRCodeDimension; Row++)
            for (int Col = 0; Col < this.QRCodeDimension; Col += 3)
                if ((this.MaskMatrix[Row, Col] & NonData) == 0)
                    this.MaskMatrix[Row, Col] ^= 1;
    }

    ////////////////////////////////////////////////////////////////////
    // Apply Mask 3
    // (row + column) % 3 == 0
    ////////////////////////////////////////////////////////////////////
    internal void ApplyMask3()
    {
        for (int Row = 0; Row < this.QRCodeDimension; Row += 3)
            for (int Col = 0; Col < this.QRCodeDimension; Col += 3)
            {
                if ((this.MaskMatrix[Row, Col] & NonData) == 0)
                    this.MaskMatrix[Row, Col] ^= 1;
                if ((this.MaskMatrix[Row + 1, Col + 2] & NonData) == 0)
                    this.MaskMatrix[Row + 1, Col + 2] ^= 1;
                if ((this.MaskMatrix[Row + 2, Col + 1] & NonData) == 0)
                    this.MaskMatrix[Row + 2, Col + 1] ^= 1;
            }
    }

    ////////////////////////////////////////////////////////////////////
    // Apply Mask 4
    // ((row / 2) + (column / 3)) % 2 == 0
    ////////////////////////////////////////////////////////////////////
    internal void ApplyMask4()
    {
        for (int Row = 0; Row < this.QRCodeDimension; Row += 4)
            for (int Col = 0; Col < this.QRCodeDimension; Col += 6)
            {
                if ((this.MaskMatrix[Row, Col] & NonData) == 0)
                    this.MaskMatrix[Row, Col] ^= 1;
                if ((this.MaskMatrix[Row, Col + 1] & NonData) == 0)
                    this.MaskMatrix[Row, Col + 1] ^= 1;
                if ((this.MaskMatrix[Row, Col + 2] & NonData) == 0)
                    this.MaskMatrix[Row, Col + 2] ^= 1;

                if ((this.MaskMatrix[Row + 1, Col] & NonData) == 0)
                    this.MaskMatrix[Row + 1, Col] ^= 1;
                if ((this.MaskMatrix[Row + 1, Col + 1] & NonData) == 0)
                    this.MaskMatrix[Row + 1, Col + 1] ^= 1;
                if ((this.MaskMatrix[Row + 1, Col + 2] & NonData) == 0)
                    this.MaskMatrix[Row + 1, Col + 2] ^= 1;

                if ((this.MaskMatrix[Row + 2, Col + 3] & NonData) == 0)
                    this.MaskMatrix[Row + 2, Col + 3] ^= 1;
                if ((this.MaskMatrix[Row + 2, Col + 4] & NonData) == 0)
                    this.MaskMatrix[Row + 2, Col + 4] ^= 1;
                if ((this.MaskMatrix[Row + 2, Col + 5] & NonData) == 0)
                    this.MaskMatrix[Row + 2, Col + 5] ^= 1;

                if ((this.MaskMatrix[Row + 3, Col + 3] & NonData) == 0)
                    this.MaskMatrix[Row + 3, Col + 3] ^= 1;
                if ((this.MaskMatrix[Row + 3, Col + 4] & NonData) == 0)
                    this.MaskMatrix[Row + 3, Col + 4] ^= 1;
                if ((this.MaskMatrix[Row + 3, Col + 5] & NonData) == 0)
                    this.MaskMatrix[Row + 3, Col + 5] ^= 1;
            }
    }

    ////////////////////////////////////////////////////////////////////
    // Apply Mask 5
    // ((row * column) % 2) + ((row * column) % 3) == 0
    ////////////////////////////////////////////////////////////////////
    internal void ApplyMask5()
    {
        for (int Row = 0; Row < this.QRCodeDimension; Row += 6)
            for (int Col = 0; Col < this.QRCodeDimension; Col += 6)
            {
                for (int Delta = 0; Delta < 6; Delta++)
                    if ((this.MaskMatrix[Row, Col + Delta] & NonData) == 0)
                        this.MaskMatrix[Row, Col + Delta] ^= 1;
                for (int Delta = 1; Delta < 6; Delta++)
                    if ((this.MaskMatrix[Row + Delta, Col] & NonData) == 0)
                        this.MaskMatrix[Row + Delta, Col] ^= 1;
                if ((this.MaskMatrix[Row + 2, Col + 3] & NonData) == 0)
                    this.MaskMatrix[Row + 2, Col + 3] ^= 1;
                if ((this.MaskMatrix[Row + 3, Col + 2] & NonData) == 0)
                    this.MaskMatrix[Row + 3, Col + 2] ^= 1;
                if ((this.MaskMatrix[Row + 3, Col + 4] & NonData) == 0)
                    this.MaskMatrix[Row + 3, Col + 4] ^= 1;
                if ((this.MaskMatrix[Row + 4, Col + 3] & NonData) == 0)
                    this.MaskMatrix[Row + 4, Col + 3] ^= 1;
            }
    }

    ////////////////////////////////////////////////////////////////////
    // Apply Mask 6
    // (((row * column) % 2) + ((row * column) mod 3)) mod 2 == 0
    ////////////////////////////////////////////////////////////////////
    internal void ApplyMask6()
    {
        for (int Row = 0; Row < this.QRCodeDimension; Row += 6)
            for (int Col = 0; Col < this.QRCodeDimension; Col += 6)
            {
                for (int Delta = 0; Delta < 6; Delta++)
                    if ((this.MaskMatrix[Row, Col + Delta] & NonData) == 0)
                        this.MaskMatrix[Row, Col + Delta] ^= 1;
                for (int Delta = 1; Delta < 6; Delta++)
                    if ((this.MaskMatrix[Row + Delta, Col] & NonData) == 0)
                        this.MaskMatrix[Row + Delta, Col] ^= 1;
                if ((this.MaskMatrix[Row + 1, Col + 1] & NonData) == 0)
                    this.MaskMatrix[Row + 1, Col + 1] ^= 1;
                if ((this.MaskMatrix[Row + 1, Col + 2] & NonData) == 0)
                    this.MaskMatrix[Row + 1, Col + 2] ^= 1;
                if ((this.MaskMatrix[Row + 2, Col + 1] & NonData) == 0)
                    this.MaskMatrix[Row + 2, Col + 1] ^= 1;
                if ((this.MaskMatrix[Row + 2, Col + 3] & NonData) == 0)
                    this.MaskMatrix[Row + 2, Col + 3] ^= 1;
                if ((this.MaskMatrix[Row + 2, Col + 4] & NonData) == 0)
                    this.MaskMatrix[Row + 2, Col + 4] ^= 1;
                if ((this.MaskMatrix[Row + 3, Col + 2] & NonData) == 0)
                    this.MaskMatrix[Row + 3, Col + 2] ^= 1;
                if ((this.MaskMatrix[Row + 3, Col + 4] & NonData) == 0)
                    this.MaskMatrix[Row + 3, Col + 4] ^= 1;
                if ((this.MaskMatrix[Row + 4, Col + 2] & NonData) == 0)
                    this.MaskMatrix[Row + 4, Col + 2] ^= 1;
                if ((this.MaskMatrix[Row + 4, Col + 3] & NonData) == 0)
                    this.MaskMatrix[Row + 4, Col + 3] ^= 1;
                if ((this.MaskMatrix[Row + 4, Col + 5] & NonData) == 0)
                    this.MaskMatrix[Row + 4, Col + 5] ^= 1;
                if ((this.MaskMatrix[Row + 5, Col + 4] & NonData) == 0)
                    this.MaskMatrix[Row + 5, Col + 4] ^= 1;
                if ((this.MaskMatrix[Row + 5, Col + 5] & NonData) == 0)
                    this.MaskMatrix[Row + 5, Col + 5] ^= 1;
            }
    }

    ////////////////////////////////////////////////////////////////////
    // Apply Mask 7
    // (((row + column) % 2) + ((row * column) mod 3)) mod 2 == 0
    ////////////////////////////////////////////////////////////////////
    private void ApplyMask7()
    {
        for (int Row = 0; Row < this.QRCodeDimension; Row += 6)
            for (int Col = 0; Col < this.QRCodeDimension; Col += 6)
            {
                if ((this.MaskMatrix[Row, Col] & NonData) == 0)
                    this.MaskMatrix[Row, Col] ^= 1;
                if ((this.MaskMatrix[Row, Col + 2] & NonData) == 0)
                    this.MaskMatrix[Row, Col + 2] ^= 1;
                if ((this.MaskMatrix[Row, Col + 4] & NonData) == 0)
                    this.MaskMatrix[Row, Col + 4] ^= 1;

                if ((this.MaskMatrix[Row + 1, Col + 3] & NonData) == 0)
                    this.MaskMatrix[Row + 1, Col + 3] ^= 1;
                if ((this.MaskMatrix[Row + 1, Col + 4] & NonData) == 0)
                    this.MaskMatrix[Row + 1, Col + 4] ^= 1;
                if ((this.MaskMatrix[Row + 1, Col + 5] & NonData) == 0)
                    this.MaskMatrix[Row + 1, Col + 5] ^= 1;

                if ((this.MaskMatrix[Row + 2, Col] & NonData) == 0)
                    this.MaskMatrix[Row + 2, Col] ^= 1;
                if ((this.MaskMatrix[Row + 2, Col + 4] & NonData) == 0)
                    this.MaskMatrix[Row + 2, Col + 4] ^= 1;
                if ((this.MaskMatrix[Row + 2, Col + 5] & NonData) == 0)
                    this.MaskMatrix[Row + 2, Col + 5] ^= 1;

                if ((this.MaskMatrix[Row + 3, Col + 1] & NonData) == 0)
                    this.MaskMatrix[Row + 3, Col + 1] ^= 1;
                if ((this.MaskMatrix[Row + 3, Col + 3] & NonData) == 0)
                    this.MaskMatrix[Row + 3, Col + 3] ^= 1;
                if ((this.MaskMatrix[Row + 3, Col + 5] & NonData) == 0)
                    this.MaskMatrix[Row + 3, Col + 5] ^= 1;

                if ((this.MaskMatrix[Row + 4, Col] & NonData) == 0)
                    this.MaskMatrix[Row + 4, Col] ^= 1;
                if ((this.MaskMatrix[Row + 4, Col + 1] & NonData) == 0)
                    this.MaskMatrix[Row + 4, Col + 1] ^= 1;
                if ((this.MaskMatrix[Row + 4, Col + 2] & NonData) == 0)
                    this.MaskMatrix[Row + 4, Col + 2] ^= 1;

                if ((this.MaskMatrix[Row + 5, Col + 1] & NonData) == 0)
                    this.MaskMatrix[Row + 5, Col + 1] ^= 1;
                if ((this.MaskMatrix[Row + 5, Col + 2] & NonData) == 0)
                    this.MaskMatrix[Row + 5, Col + 2] ^= 1;
                if ((this.MaskMatrix[Row + 5, Col + 3] & NonData) == 0)
                    this.MaskMatrix[Row + 5, Col + 3] ^= 1;
            }
    }
}