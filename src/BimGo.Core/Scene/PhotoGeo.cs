// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// Site photos round: a photo pinned by hand that also has GPS: where the GPS says it is, and where it really is in
    /// the model (Revit internal metres).
    /// </summary>
    public readonly record struct PhotoAnchor(double Latitude, double Longitude, double X, double Y);

    /// <summary>
    /// Site photos round: GPS → model. A rigid fit (turn and shift, no scale) from local east / north metres around a
    /// reference to the model's internal X / Y: from one anchor with the model's true north, from two or more by least
    /// squares (the turn too, so a model without a true north set still lines up).
    /// </summary>
    public readonly struct PhotoGeoFit
    {
        internal PhotoGeoFit(double refLatitude, double refLongitude, double angle, double tx, double ty, int anchors, double rms, bool fromNorth)
        {
            RefLatitude = refLatitude;
            RefLongitude = refLongitude;
            Angle = angle;
            Cos = Math.Cos(angle);
            Sin = Math.Sin(angle);
            Tx = tx;
            Ty = ty;
            Anchors = anchors;
            Rms = rms;
            FromNorth = fromNorth;
        }

        /// <summary>The reference the local metres are measured from.</summary>
        public double RefLatitude { get; }

        /// <summary>The reference the local metres are measured from.</summary>
        public double RefLongitude { get; }

        /// <summary>East / north → model axes, counter-clockwise (radians).</summary>
        public double Angle { get; }

        /// <summary>cos / sin of <see cref="Angle"/>.</summary>
        public double Cos { get; }

        /// <summary>cos / sin of <see cref="Angle"/>.</summary>
        public double Sin { get; }

        /// <summary>The shift (model metres).</summary>
        public double Tx { get; }

        /// <summary>The shift (model metres).</summary>
        public double Ty { get; }

        /// <summary>How many anchors it was fitted to.</summary>
        public int Anchors { get; }

        /// <summary>The anchors' root mean square misfit (m; 0 with one anchor).</summary>
        public double Rms { get; }

        /// <summary>True when the turn came from the model's true north (one anchor).</summary>
        public bool FromNorth { get; }

        /// <summary>A GPS position in the model (internal X / Y, metres).</summary>
        public void Apply(double latitude, double longitude, out double x, out double y)
        {
            PhotoGeo.ToLocal(latitude, longitude, RefLatitude, RefLongitude, out double east, out double north);
            x = Cos * east - Sin * north + Tx;
            y = Sin * east + Cos * north + Ty;
        }

        /// <summary>A compass heading (degrees, clockwise from north) as a model yaw (radians, 0 = +X, counter-clockwise).</summary>
        public float YawOf(double heading)
        {
            double h = heading * Math.PI / 180.0;
            double east = Math.Sin(h), north = Math.Cos(h);
            double x = Cos * east - Sin * north, y = Sin * east + Cos * north;
            return (float)Math.Atan2(y, x);
        }
    }

    /// <summary>
    /// Site photos round: latitude / longitude to local metres, the GPS → model fit, and a photo's field of view from its
    /// 35 mm equivalent focal length.
    /// </summary>
    public static class PhotoGeo
    {
        /// <summary>Mean earth radius (m).</summary>
        private const double EARTH_RADIUS = 6371008.8;

        /// <summary>The 35 mm frame's diagonal (mm): equivalent focal lengths are defined by it.</summary>
        private const double FRAME_DIAGONAL = 43.2666;

        /// <summary>
        /// East / north metres from a reference (an equirectangular projection: well under a centimetre of error per
        /// 100 m across a site).
        /// </summary>
        public static void ToLocal(double latitude, double longitude, double refLatitude, double refLongitude, out double east, out double north)
        {
            double lat0 = refLatitude * Math.PI / 180.0;
            double dLon = longitude - refLongitude;
            if (dLon > 180) { dLon -= 360; }
            if (dLon < -180) { dLon += 360; }
            east = dLon * Math.PI / 180.0 * EARTH_RADIUS * Math.Cos(lat0);
            north = (latitude - refLatitude) * Math.PI / 180.0 * EARTH_RADIUS;
        }

        /// <summary>
        /// Fits GPS to the model from the anchors: one anchor turns by the model's true north
        /// (<paramref name="northAngle"/>, the internal → shared angle as <see cref="SolarPosition.NorthAngle"/> gives it);
        /// two or more fit the turn by least squares. Anchors within 1 m of their middle count as one (a turn fitted to
        /// them would only be GPS noise).
        /// </summary>
        /// <returns>False with no anchors.</returns>
        public static bool TryFit(IReadOnlyList<PhotoAnchor> anchors, double northAngle, out PhotoGeoFit fit)
        {
            fit = default;
            if (anchors == null || anchors.Count == 0) { return false; }

            double refLat = 0, refLon = 0;
            foreach (PhotoAnchor a in anchors)
            {
                refLat += a.Latitude;
                refLon += a.Longitude;
            }
            refLat /= anchors.Count;
            refLon /= anchors.Count;

            int n = anchors.Count;
            var east = new double[n];
            var north = new double[n];
            double pe = 0, pn = 0, qx = 0, qy = 0;
            for (int i = 0; i < n; i++)
            {
                ToLocal(anchors[i].Latitude, anchors[i].Longitude, refLat, refLon, out east[i], out north[i]);
                pe += east[i];
                pn += north[i];
                qx += anchors[i].X;
                qy += anchors[i].Y;
            }
            pe /= n;
            pn /= n;
            qx /= n;
            qy /= n;

            // The spread of the GPS points: too small to fit a turn from
            double spread = 0;
            for (int i = 0; i < n; i++) { spread = Math.Max(spread, Math.Sqrt((east[i] - pe) * (east[i] - pe) + (north[i] - pn) * (north[i] - pn))); }

            double angle;
            bool fromNorth = n < 2 || spread < 1.0;
            if (fromNorth)
            {
                angle = -northAngle; // true north → internal, as SolarPosition.ToModel
            }
            else
            {
                double cross = 0, dot = 0;
                for (int i = 0; i < n; i++)
                {
                    double ax = east[i] - pe, ay = north[i] - pn;
                    double bx = anchors[i].X - qx, by = anchors[i].Y - qy;
                    dot += ax * bx + ay * by;
                    cross += ax * by - ay * bx;
                }
                angle = Math.Atan2(cross, dot);
            }

            double c = Math.Cos(angle), s = Math.Sin(angle);
            double tx = qx - (c * pe - s * pn), ty = qy - (s * pe + c * pn);
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                double x = c * east[i] - s * north[i] + tx, y = s * east[i] + c * north[i] + ty;
                sum += (x - anchors[i].X) * (x - anchors[i].X) + (y - anchors[i].Y) * (y - anchors[i].Y);
            }
            fit = new PhotoGeoFit(refLat, refLon, angle, tx, ty, n, Math.Sqrt(sum / n), fromNorth);
            return true;
        }

        /// <summary>
        /// How far the compass is off (radians, added to <see cref="PhotoGeoFit.YawOf"/>), from photos pinned by hand that
        /// also recorded a heading: the circular mean of pinned yaw − compass yaw. It takes up the magnetic declination
        /// (phones often write magnetic headings) and a phone's own compass error. 0 with none.
        /// </summary>
        /// <param name="pairs">Per pinned photo: the yaw it was pinned at, and the yaw its compass heading gives.</param>
        public static float HeadingCorrection(IEnumerable<(float Pinned, float Compass)> pairs)
        {
            double x = 0, y = 0;
            int n = 0;
            if (pairs != null)
            {
                foreach ((float pinned, float compass) in pairs)
                {
                    double d = pinned - compass;
                    x += Math.Cos(d);
                    y += Math.Sin(d);
                    n++;
                }
            }
            return n == 0 || (x * x + y * y) < 1e-12 ? 0f : (float)Math.Atan2(y, x);
        }

        /// <summary>
        /// The horizontal field of view (degrees) of a picture <paramref name="width"/> × <paramref name="height"/> taken at
        /// a 35 mm equivalent focal length (defined on the frame's diagonal, so 4:3 phone pictures come out right), or null.
        /// </summary>
        public static float? FieldOfView(double? focalLength35, int width, int height)
        {
            if (focalLength35 is not double f || f < 1 || width <= 0 || height <= 0) { return null; }
            double halfDiagonal = Math.Atan(FRAME_DIAGONAL / (2.0 * f));
            double diagonal = Math.Sqrt((double)width * width + (double)height * height);
            double halfWidth = Math.Atan(Math.Tan(halfDiagonal) * width / diagonal);
            return (float)(2.0 * halfWidth * 180.0 / Math.PI);
        }
    }
}
