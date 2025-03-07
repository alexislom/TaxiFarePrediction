﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.ML;
using Microsoft.ML.Data;
using PLplot;
using TaxiFarePrediction.DataStructures;

namespace TaxiFarePrediction
{
    internal static class Program
    {
        private static string AppPath => Path.GetDirectoryName(Environment.GetCommandLineArgs()[0]);

        private static readonly string BaseDatasetsRelativePath = @"../../../../Data";
        private static readonly string TrainDataRelativePath = $"{BaseDatasetsRelativePath}/taxi-fare-train.csv";
        private static readonly string TestDataRelativePath = $"{BaseDatasetsRelativePath}/taxi-fare-test.csv";

        private static readonly string TrainDataPath = PathHelper.GetAbsolutePath(TrainDataRelativePath);
        private static readonly string TestDataPath = PathHelper.GetAbsolutePath(TestDataRelativePath);

        private static readonly string BaseModelsRelativePath = @"../../../../MLModels";
        private static readonly string ModelRelativePath = $"{BaseModelsRelativePath}/TaxiFareModel.zip";

        private static readonly string ModelPath = PathHelper.GetAbsolutePath(ModelRelativePath);

        static void Main(string[] args)
        {
            var mlContext = new MLContext(seed: 0);

            // Create, Train, Evaluate and Save a model
            BuildTrainEvaluateAndSaveModel(mlContext);

            // Make a single test prediction loding the model from .ZIP file
            TestSinglePrediction(mlContext);

            // Paint regression distribution chart for a number of elements read from a Test DataSet file
            PlotRegressionChart(mlContext, TestDataPath, 100, args);

            Console.WriteLine("Press any key to exit..");
            Console.ReadLine();
        }

        private static void BuildTrainEvaluateAndSaveModel(MLContext mlContext)
        {
            // STEP 1: Common data loading configuration
            var baseTrainingDataView = mlContext.Data.LoadFromTextFile<TaxiTrip>(TrainDataPath, hasHeader: true, separatorChar: ',');
            var testDataView = mlContext.Data.LoadFromTextFile<TaxiTrip>(TestDataPath, hasHeader: true, separatorChar: ',');

            //Removing extreme data for FareAmounts higher than $150 and lower than $1 which can be error-data
            var cnt = baseTrainingDataView.GetColumn<float>(nameof(TaxiTrip.FareAmount)).Count();
            var trainingDataView = mlContext.Data.FilterRowsByColumn(baseTrainingDataView, nameof(TaxiTrip.FareAmount), lowerBound: 1, upperBound: 150);
            var cnt2 = trainingDataView.GetColumn<float>(nameof(TaxiTrip.FareAmount)).Count();

            // STEP 2: Common data process configuration with pipeline data transformations
            var dataProcessPipeline = mlContext.Transforms.CopyColumns(outputColumnName: "Label", inputColumnName: nameof(TaxiTrip.FareAmount))
                            .Append(mlContext.Transforms.Categorical.OneHotEncoding(outputColumnName: "VendorIdEncoded", inputColumnName: nameof(TaxiTrip.VendorId)))
                            .Append(mlContext.Transforms.Categorical.OneHotEncoding(outputColumnName: "RateCodeEncoded", inputColumnName: nameof(TaxiTrip.RateCode)))
                            .Append(mlContext.Transforms.Categorical.OneHotEncoding(outputColumnName: "PaymentTypeEncoded", inputColumnName: nameof(TaxiTrip.PaymentType)))
                            .Append(mlContext.Transforms.NormalizeMeanVariance(outputColumnName: nameof(TaxiTrip.PassengerCount)))
                            .Append(mlContext.Transforms.NormalizeMeanVariance(outputColumnName: nameof(TaxiTrip.TripTime)))
                            .Append(mlContext.Transforms.NormalizeMeanVariance(outputColumnName: nameof(TaxiTrip.TripDistance)))
                            .Append(mlContext.Transforms.Concatenate("Features", "VendorIdEncoded", "RateCodeEncoded", "PaymentTypeEncoded", nameof(TaxiTrip.PassengerCount)
                            , nameof(TaxiTrip.TripTime), nameof(TaxiTrip.TripDistance)));

            // (OPTIONAL) Peek data (such as 5 records) in training DataView after applying the ProcessPipeline's transformations into "Features"
            ConsoleHelper.PeekDataViewInConsole(mlContext, trainingDataView, dataProcessPipeline, 5);
            ConsoleHelper.PeekVectorColumnDataInConsole(mlContext, "Features", trainingDataView, dataProcessPipeline, 5);

            // STEP 3: Set the training algorithm, then create and config the modelBuilder - Selected Trainer (SDCA Regression algorithm)
            var trainer = mlContext.Regression.Trainers.Sdca(labelColumnName: "Label", featureColumnName: "Features");
            var trainingPipeline = dataProcessPipeline.Append(trainer);

            // STEP 4: Train the model fitting to the DataSet
            //The pipeline is trained on the dataset that has been loaded and transformed.
            Console.WriteLine("=============== Training the model ===============");
            var trainedModel = trainingPipeline.Fit(trainingDataView);

            // STEP 5: Evaluate the model and show accuracy stats
            Console.WriteLine("===== Evaluating Model's accuracy with Test data =====");

            var predictions = trainedModel.Transform(testDataView);
            var metrics = mlContext.Regression.Evaluate(predictions, labelColumnName: "Label", scoreColumnName: "Score");

            ConsoleHelper.PrintRegressionMetrics(trainer.ToString(), metrics);

            // STEP 6: Save/persist the trained model to a .ZIP file
            mlContext.Model.Save(trainedModel, trainingDataView.Schema, ModelPath);

            Console.WriteLine($"The model is saved to {ModelPath}");
        }

        private static void TestSinglePrediction(MLContext mlContext)
        {
            //vendor_id,rate_code,passenger_count,trip_time_in_secs,trip_distance,payment_type,fare_amount
            //VTS,1,1,1140,3.75,CRD,15.5

            var taxiTripSample = new TaxiTrip
            {
                VendorId = "VTS",
                RateCode = "1",
                PassengerCount = 1,
                TripTime = 1140,
                TripDistance = 3.75f,
                PaymentType = "CRD",
                FareAmount = 0 // To predict. Actual/Observed = 15.5
            };

            // Load trained model
            var trainedModel = mlContext.Model.Load(ModelPath, out var modelInputSchema);

            // Create prediction engine related to the loaded trained model
            var predEngine = mlContext.Model.CreatePredictionEngine<TaxiTrip, TaxiTripFarePrediction>(trainedModel);

            //Score
            var resultprediction = predEngine.Predict(taxiTripSample);

            Console.WriteLine($"**********************************************************************");
            Console.WriteLine($"Predicted fare: {resultprediction.FareAmount:0.#####}, actual fare: 15.5");
            Console.WriteLine($"**********************************************************************");
        }

        private static void PlotRegressionChart(MLContext mlContext,
                                                string testDataSetPath,
                                                int numberOfRecordsToRead,
                                                string[] args)
        {
            ITransformer trainedModel;
            using (var stream = new FileStream(ModelPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                trainedModel = mlContext.Model.Load(stream, out var modelInputSchema);
            }

            // Create prediction engine related to the loaded trained model
            var predFunction = mlContext.Model.CreatePredictionEngine<TaxiTrip, TaxiTripFarePrediction>(trainedModel);

            string chartFileName = string.Empty;
            using (var pl = new PLStream())
            {
                // use SVG backend and write to SineWaves.svg in current directory
                if (args.Length == 1 && args[0] == "svg")
                {
                    pl.sdev("svg");
                    chartFileName = "TaxiRegressionDistribution.svg";
                    pl.sfnam(chartFileName);
                }
                else
                {
                    pl.sdev("pngcairo");
                    chartFileName = "TaxiRegressionDistribution.png";
                    pl.sfnam(chartFileName);
                }

                // use white background with black foreground
                pl.spal0("cmap0_alternate.pal");

                // Initialize plplot
                pl.init();

                // set axis limits
                const int xMinLimit = 0;
                const int xMaxLimit = 35; //Rides larger than $35 are not shown in the chart
                const int yMinLimit = 0;
                const int yMaxLimit = 35;  //Rides larger than $35 are not shown in the chart
                pl.env(xMinLimit, xMaxLimit, yMinLimit, yMaxLimit, AxesScale.Independent, AxisBox.BoxTicksLabelsAxes);

                // Set scaling for mail title text 125% size of default
                pl.schr(0, 1.25);

                // The main title
                pl.lab("Measured", "Predicted", "Distribution of Taxi Fare Prediction");

                pl.col0(1);

                int totalNumber = numberOfRecordsToRead;
                var testData = TaxiTripCsvReader.GetDataFromCsv(testDataSetPath, totalNumber).ToList();

                //This code is the symbol to paint
                char code = (char)9;

                // plot using other color
                //pl.col0(9); //Light Green
                //pl.col0(4); //Red
                pl.col0(2); //Blue

                double yTotal = 0;
                double xTotal = 0;
                double xyMultiTotal = 0;
                double xSquareTotal = 0;

                for (int i = 0; i < testData.Count; i++)
                {
                    var x = new double[1];
                    var y = new double[1];

                    //Make Prediction
                    var FarePrediction = predFunction.Predict(testData[i]);

                    x[0] = testData[i].FareAmount;
                    y[0] = FarePrediction.FareAmount;

                    //Paint a dot
                    pl.poin(x, y, code);

                    xTotal += x[0];
                    yTotal += y[0];

                    double multi = x[0] * y[0];
                    xyMultiTotal += multi;

                    double xSquare = x[0] * x[0];
                    xSquareTotal += xSquare;

                    double ySquare = y[0] * y[0];

                    Console.WriteLine($"-------------------------------------------------");
                    Console.WriteLine($"Predicted : {FarePrediction.FareAmount}");
                    Console.WriteLine($"Actual:    {testData[i].FareAmount}");
                    Console.WriteLine($"-------------------------------------------------");
                }

                // Regression Line calculation explanation:
                // https://www.khanacademy.org/math/statistics-probability/describing-relationships-quantitative-data/more-on-regression/v/regression-line-example

                double minY = yTotal / totalNumber;
                double minX = xTotal / totalNumber;
                double minXY = xyMultiTotal / totalNumber;
                double minXsquare = xSquareTotal / totalNumber;

                double m = ((minX * minY) - minXY) / ((minX * minX) - minXsquare);

                double b = minY - (m * minX);

                //Generic function for Y for the regression line
                // y = (m * x) + b;

                double x1 = 1;
                //Function for Y1 in the line
                double y1 = (m * x1) + b;

                double x2 = 39;
                //Function for Y2 in the line
                double y2 = (m * x2) + b;

                var xArray = new double[2];
                var yArray = new double[2];
                xArray[0] = x1;
                yArray[0] = y1;
                xArray[1] = x2;
                yArray[1] = y2;

                pl.col0(4);
                pl.line(xArray, yArray);

                // end page (writes output to disk)
                pl.eop();
            }

            // Open Chart File In Microsoft Photos App (Or default app, like browser for .svg)

            Console.WriteLine("Showing chart...");
            var p = new Process();
            var chartFileNamePath = @".\" + chartFileName;
            p.StartInfo = new ProcessStartInfo(chartFileNamePath)
            {
                UseShellExecute = true
            };
            p.Start();
        }
    }
}