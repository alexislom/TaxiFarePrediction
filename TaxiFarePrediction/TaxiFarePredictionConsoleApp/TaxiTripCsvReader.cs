using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TaxiFarePrediction.DataStructures;

namespace TaxiFarePrediction
{
    public static class TaxiTripCsvReader
    {
        public static IEnumerable<TaxiTrip> GetDataFromCsv(string dataLocation, int numMaxRecords)
        {
            var records =
                File.ReadAllLines(dataLocation)
                    .Skip(1)
                    .Select(x => x.Split(','))
                    .Select(x => new TaxiTrip
                    {
                        VendorId = x[0],
                        RateCode = x[1],
                        PassengerCount = float.Parse(x[2], CultureInfo.InvariantCulture),
                        TripTime = float.Parse(x[3], CultureInfo.InvariantCulture),
                        TripDistance = float.Parse(x[4], CultureInfo.InvariantCulture),
                        PaymentType = x[5],
                        FareAmount = float.Parse(x[6], CultureInfo.InvariantCulture)
                    })
                    .Take(numMaxRecords);

            return records;
        }
    }
}