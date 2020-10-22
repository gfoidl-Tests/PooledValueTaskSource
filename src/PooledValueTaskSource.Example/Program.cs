﻿using System;
using System.IO;
using System.Threading.Tasks;

namespace PooledValueTaskSource
{
    class Program
    {
        private const string FileName = "foo.txt";

        static async Task Main(string[] args)
        {
            Engine eng = new Engine();

            Func<string, ValueTask<string>>[] operations =
            {
                eng.ReadFileAsync3,
                eng.ReadFileAsync4
            };

            foreach (Func<string, ValueTask<string>> operation in operations)
            {
                string content = await operation(FileName);
                Console.WriteLine("Result: " + content);

                try
                {
                    var task = operation(FileName);
                    string content2 = await task;
                    string content3 = await task;
                    Console.WriteLine("Result: " + content2 + content3);
                }
                catch (InvalidOperationException e)
                {
                    Console.WriteLine(e);
                }

                string content4 = await operation(@"c:\dummy.txt");
                Console.WriteLine("Result: " + content4);

                string content5 = await operation(FileName);
                Console.WriteLine("Result: " + content5);

                var task2 = operation(FileName);
                await Task.Delay(2000);
                string content6 = await task2;
                Console.WriteLine("Result: " + content6);

                Console.WriteLine(new string('-', 80));
            }
        }
    }

    public class Engine
    {
        public Task<string> ReadFileAsync(string filename)
        {
            if (!File.Exists(filename))
                return Task.FromResult(string.Empty);
            return File.ReadAllTextAsync(filename);
        }

        public async ValueTask<string> ReadFileAsync1(string filename)
        {
            if (!File.Exists(filename))
                return string.Empty;
            return await File.ReadAllTextAsync(filename);
        }

        public ValueTask<string> ReadFileAsync2(string filename)
        {
            if (!File.Exists(filename))
                return new ValueTask<string>(string.Empty);
            return new ValueTask<string>(File.ReadAllTextAsync(filename));
        }

        public ValueTask<string> ReadFileAsync3(string filename)
        {
            if (!File.Exists(filename))
                return new ValueTask<string>("!");
            var cachedOp = _pool.Rent();
            return cachedOp.RunAsync(filename, _pool);
        }

        public ValueTask<string> ReadFileAsync4(string filename)
        {
            if (!File.Exists(filename))
                return new ValueTask<string>("!");
            var cachedOp = _pool2.Rent();
            return cachedOp.RunAsync(filename, _pool2);
        }

        private readonly ObjectPool<FileReadingPooledValueTaskSource> _pool = new ObjectPool<FileReadingPooledValueTaskSource>(() => new FileReadingPooledValueTaskSource(), 10);
        private readonly ObjectPool<FileReadingPooledValueTaskSource2> _pool2 = new ObjectPool<FileReadingPooledValueTaskSource2>(() => new FileReadingPooledValueTaskSource2(), 10);
    }
}
