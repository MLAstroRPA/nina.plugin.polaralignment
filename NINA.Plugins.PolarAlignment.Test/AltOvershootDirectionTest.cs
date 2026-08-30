using FluentAssertions;
using NINA.Astrometry;
using NINA.Core.Utility;

namespace NINA.Plugins.PolarAlignment.Test {
    public class AltOvershootDirectionTest {
        /// <summary>
        /// Verifies that <see cref="PolarErrorDetermination.AltitudeCorrectionIsUp"/> follows the
        /// direction reported on screen by the solve (Northern/Southern hemisphere + error sign),
        /// which is the reference used to pick the MLAstroRPA "Run overshoot for moving Up/Down"
        /// checkbox — independently of the motor command direction.
        /// </summary>
        private static PolarErrorDetermination CreateError(double latitudeDegrees) {
            var latitude = Angle.ByDegree(latitudeDegrees);
            var longitude = Angle.ByDegree(7);
            var elevation = 250d;
            var time = new CustomTime(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var refraction = new RefractionParameters(0, 0.0001, 0, 0);

            var solve1 = new Coordinates(Angle.ByDegree(20), Angle.ByDegree(40), Epoch.JNOW, time).Transform(Epoch.J2000);
            var position1 = new Position(solve1, 0, latitude, longitude, elevation, refraction);

            var solve2 = new Coordinates(Angle.ByDegree(60), Angle.ByDegree(40), Epoch.JNOW, time).Transform(Epoch.J2000);
            var position2 = new Position(solve2, 0, latitude, longitude, elevation, refraction);

            var solve3 = new Coordinates(Angle.ByDegree(90), Angle.ByDegree(40), Epoch.JNOW, time).Transform(Epoch.J2000);
            var position3 = new Position(solve3, 0, latitude, longitude, elevation, refraction);

            return new PolarErrorDetermination(
                new PlateSolving.PlateSolveResult() { Coordinates = solve1 },
                position3, position2, position1, latitude, longitude, elevation, refraction, true, 0d);
        }

        [Test]
        public void Northern_ErrorPositive_IsNotUp_AndShowsDown() {
            var error = CreateError(49);
            error.CurrentMountAxisAltitudeError = Angle.ByDegree(1);
            error.AltitudeCorrectionIsUp.Should().BeFalse();
            error.CurrentMountAxisAltitudeErrorDirection.Should().Contain("down");
        }

        [Test]
        public void Northern_ErrorNegative_IsUp_AndShowsUp() {
            var error = CreateError(49);
            error.CurrentMountAxisAltitudeError = Angle.ByDegree(-1);
            error.AltitudeCorrectionIsUp.Should().BeTrue();
            error.CurrentMountAxisAltitudeErrorDirection.Should().Contain("up");
        }

        [Test]
        public void Southern_ErrorPositive_IsUp_AndShowsUp() {
            var error = CreateError(-49);
            error.CurrentMountAxisAltitudeError = Angle.ByDegree(1);
            error.AltitudeCorrectionIsUp.Should().BeTrue();
            error.CurrentMountAxisAltitudeErrorDirection.Should().Contain("up");
        }

        [Test]
        public void Southern_ErrorNegative_IsNotUp_AndShowsDown() {
            var error = CreateError(-49);
            error.CurrentMountAxisAltitudeError = Angle.ByDegree(-1);
            error.AltitudeCorrectionIsUp.Should().BeFalse();
            error.CurrentMountAxisAltitudeErrorDirection.Should().Contain("down");
        }

        [Test]
        public void ZeroError_IsNotUp_AndShowsEmpty() {
            var error = CreateError(49);
            error.CurrentMountAxisAltitudeError = Angle.ByDegree(0);
            error.AltitudeCorrectionIsUp.Should().BeFalse();
            error.CurrentMountAxisAltitudeErrorDirection.Should().BeEmpty();
        }

        class CustomTime : ICustomDateTime {
            DateTime time;
            public CustomTime(DateTime time) {
                this.time = time;
            }
            public DateTime Now => time;
            public DateTime UtcNow => time;
        }
    }
}
