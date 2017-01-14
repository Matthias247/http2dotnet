using Xunit;
using Http2.Hpack;

namespace HpackTests
{
    public class EncoderTests
    {
        const int MaxFrameSize = 65535;

        [Fact]
        public void ShouldHaveADefaultDynamicTableSizeOf4096()
        {
            var encoder = new Encoder();
            Assert.Equal(4096, encoder.DynamicTableSize);
        }

        [Fact]
        public void ShouldAllowToAdjustTheDynamicTableSizeThroughConstructor()
        {
            var encoder = new Encoder(new Encoder.Options{
                DynamicTableSize = 0,
            });
            Assert.Equal(0, encoder.DynamicTableSize);
        }

        [Fact]
        public void ShouldAllowToAdjustTheDynamicTableSizeThroughPropertySetter()
        {
            var encoder = new Encoder();
            encoder.DynamicTableSize = 200;
            Assert.Equal(200, encoder.DynamicTableSize);
        }

        [Fact]
        public void ShouldHandleExampleC2_1OfTheSpecificationCorrectly()
        {
            var encoder = new Encoder(new Encoder.Options{
                HuffmanStrategy = HuffmanStrategy.Never,
            });
            var fields = new HeaderField[] {
                new HeaderField{ Name = "custom-key", Value = "custom-header", Sensitive = false }
            };

            var result = new Buffer();
            result.AddHexString(
                "400a637573746f6d2d6b65790d637573" +
                "746f6d2d686561646572");

            var res = encoder.Encode(fields, MaxFrameSize);
            Assert.Equal(result.Bytes, res.Bytes);
            Assert.Equal(1, res.FieldCount);
            Assert.Equal(55, encoder.DynamicTableUsedSize);
            Assert.Equal(1, encoder.DynamicTableLength);
        }

        [Fact]
        public void ShouldHandleExampleC2_2OfTheSpecificationCorrectly()
        {
            var encoder = new Encoder(new Encoder.Options{
                HuffmanStrategy = HuffmanStrategy.Never,
            });
            // Decrease table size to avoid using indexing
            encoder.DynamicTableSize = 0;
            var fields = new HeaderField[] {
                new HeaderField{ Name = ":path", Value = "/sample/path", Sensitive = false }
            };

            // first item with name :path
            var result = new Buffer();
            result.AddHexString("040c2f73616d706c652f70617468");

            var res = encoder.Encode(fields, MaxFrameSize);
            Assert.Equal(result.Bytes, res.Bytes);
            Assert.Equal(1, res.FieldCount);
            Assert.Equal(0, encoder.DynamicTableUsedSize);
            Assert.Equal(0, encoder.DynamicTableLength);
        }

        [Fact]
        public void ShouldHandleExampleC2_3OfTheSpecificationCorrectly()
        {
            var encoder = new Encoder(new Encoder.Options{
                HuffmanStrategy = HuffmanStrategy.Never,
            });
            var fields = new HeaderField[] {
                new HeaderField{ Name = "password", Value = "secret", Sensitive = true }
            };

            var result = new Buffer();
            result.AddHexString("100870617373776f726406736563726574");

            var res = encoder.Encode(fields, MaxFrameSize);
            Assert.Equal(result.Bytes, res.Bytes);
            Assert.Equal(1, res.FieldCount);
            Assert.Equal(0, encoder.DynamicTableUsedSize);
            Assert.Equal(0, encoder.DynamicTableLength);
        }

        [Fact]
        public void ShouldHandleExampleC2_4OfTheSpecificationCorrectly()
        {
            var encoder = new Encoder(new Encoder.Options{
                HuffmanStrategy = HuffmanStrategy.Never,
            });
            var fields = new HeaderField[] {
                new HeaderField{ Name = ":method", Value = "GET", Sensitive = false }
            };

            var result = new Buffer();
            result.AddHexString("82");

            var res = encoder.Encode(fields, MaxFrameSize);
            Assert.Equal(result.Bytes, res.Bytes);
            Assert.Equal(1, res.FieldCount);
            Assert.Equal(0, encoder.DynamicTableUsedSize);
            Assert.Equal(0, encoder.DynamicTableLength);
        }

        [Fact]
        public void ShouldHandleExampleC3OfTheSpecificationCorrectly()
        {
            var encoder = new Encoder(new Encoder.Options{
                HuffmanStrategy = HuffmanStrategy.Never,
            });
            var fields = new HeaderField[] {
                new HeaderField{ Name = ":method", Value ="GET", Sensitive = false },
                new HeaderField{ Name = ":scheme", Value ="http", Sensitive = false },
                new HeaderField{ Name = ":path", Value ="/", Sensitive = false },
                new HeaderField{ Name = ":authority", Value ="www.example.com", Sensitive = false },
            };

            // C.3.1
            var result = new Buffer();
            result.AddHexString("828684410f7777772e6578616d706c652e636f6d");

            var res = encoder.Encode(fields, MaxFrameSize);
            Assert.Equal(result.Bytes, res.Bytes);
            Assert.Equal(4, res.FieldCount);
            Assert.Equal(57, encoder.DynamicTableUsedSize);
            Assert.Equal(1, encoder.DynamicTableLength);

            // C.3.2
            fields = new HeaderField[] {
                new HeaderField{ Name = ":method", Value ="GET", Sensitive = false },
                new HeaderField{ Name = ":scheme", Value ="http", Sensitive = false },
                new HeaderField{ Name = ":path", Value ="/", Sensitive = false },
                new HeaderField{ Name = ":authority", Value ="www.example.com", Sensitive = false },
                new HeaderField{ Name = "cache-control", Value ="no-cache", Sensitive = false },
            };
            result = new Buffer();
            result.AddHexString("828684be58086e6f2d6361636865");

            res = encoder.Encode(fields, MaxFrameSize);
            Assert.Equal(result.Bytes, res.Bytes);
            Assert.Equal(5, res.FieldCount);
            Assert.Equal(110, encoder.DynamicTableUsedSize);
            Assert.Equal(2, encoder.DynamicTableLength);

            // C.3.3
            fields = new HeaderField[] {
                new HeaderField{ Name = ":method", Value ="GET", Sensitive = false },
                new HeaderField{ Name = ":scheme", Value ="https", Sensitive = false },
                new HeaderField{ Name = ":path", Value ="/index.html", Sensitive = false },
                new HeaderField{ Name = ":authority", Value ="www.example.com", Sensitive = false },
                new HeaderField{ Name = "custom-key", Value ="custom-value", Sensitive = false },
            };
            result = new Buffer();
            result.AddHexString("828785bf400a637573746f6d2d6b65790c637573746f6d2d76616c7565");

            res = encoder.Encode(fields, MaxFrameSize);
            Assert.Equal(result.Bytes, res.Bytes);
            Assert.Equal(5, res.FieldCount);
            Assert.Equal(164, encoder.DynamicTableUsedSize);
            Assert.Equal(3, encoder.DynamicTableLength);
        }

        [Fact]
        public void ShouldHandleExampleC4OfTheSpecificationCorrectly()
        {
            var encoder = new Encoder(new Encoder.Options{
                HuffmanStrategy = HuffmanStrategy.Always,
            });
            var fields = new HeaderField[] {
                new HeaderField{ Name = ":method", Value ="GET", Sensitive = false },
                new HeaderField{ Name = ":scheme", Value ="http", Sensitive = false },
                new HeaderField{ Name = ":path", Value ="/", Sensitive = false },
                new HeaderField{ Name = ":authority", Value ="www.example.com", Sensitive = false },
            };

            // C.4.1
            var result = new Buffer();
            result.AddHexString("828684418cf1e3c2e5f23a6ba0ab90f4ff");

            var res = encoder.Encode(fields, MaxFrameSize);
            Assert.Equal(result.Bytes, res.Bytes);
            Assert.Equal(4, res.FieldCount);
            Assert.Equal(57, encoder.DynamicTableUsedSize);
            Assert.Equal(1, encoder.DynamicTableLength);

            // C.4.2
            fields = new HeaderField[] {
                new HeaderField{ Name = ":method", Value ="GET", Sensitive = false },
                new HeaderField{ Name = ":scheme", Value ="http", Sensitive = false },
                new HeaderField{ Name = ":path", Value ="/", Sensitive = false },
                new HeaderField{ Name = ":authority", Value ="www.example.com", Sensitive = false },
                new HeaderField{ Name = "cache-control", Value ="no-cache", Sensitive = false },
            };
            result = new Buffer();
            result.AddHexString("828684be5886a8eb10649cbf");

            res = encoder.Encode(fields, MaxFrameSize);
            Assert.Equal(result.Bytes, res.Bytes);
            Assert.Equal(5, res.FieldCount);
            Assert.Equal(110, encoder.DynamicTableUsedSize);
            Assert.Equal(2, encoder.DynamicTableLength);

            // C.4.3
            fields = new HeaderField[] {
                new HeaderField{ Name = ":method", Value ="GET", Sensitive = false },
                new HeaderField{ Name = ":scheme", Value ="https", Sensitive = false },
                new HeaderField{ Name = ":path", Value ="/index.html", Sensitive = false },
                new HeaderField{ Name = ":authority", Value ="www.example.com", Sensitive = false },
                new HeaderField{ Name = "custom-key", Value ="custom-value", Sensitive = false },
            };
            result = new Buffer();
            result.AddHexString("828785bf408825a849e95ba97d7f8925a849e95bb8e8b4bf");

            res = encoder.Encode(fields, MaxFrameSize);
            Assert.Equal(result.Bytes, res.Bytes);
            Assert.Equal(5, res.FieldCount);
            Assert.Equal(164, encoder.DynamicTableUsedSize);
            Assert.Equal(3, encoder.DynamicTableLength);
        }

        [Fact]
        public void ShouldHandleExampleC5OfTheSpecificationCorrectly()
        {
            var encoder = new Encoder(new Encoder.Options{
                HuffmanStrategy = HuffmanStrategy.Never,
                DynamicTableSize = 256,
            });
            var fields = new HeaderField[] {
                new HeaderField{ Name = ":status", Value ="302", Sensitive = false },
                new HeaderField{ Name = "cache-control", Value ="private", Sensitive = false },
                new HeaderField{ Name = "date", Value ="Mon, 21 Oct 2013 20:13:21 GMT", Sensitive = false },
                new HeaderField{ Name = "location", Value ="https://www.example.com", Sensitive = false },
            };

            // C.5.1
            var result = new Buffer();
            result.AddHexString(
                "4803333032580770726976617465611d" +
                "4d6f6e2c203231204f63742032303133" +
                "2032303a31333a323120474d546e1768" +
                "747470733a2f2f7777772e6578616d70" +
                "6c652e636f6d");

            var res = encoder.Encode(fields, MaxFrameSize);
            Assert.Equal(result.Bytes, res.Bytes);
            Assert.Equal(4, res.FieldCount);
            Assert.Equal(222, encoder.DynamicTableUsedSize);
            Assert.Equal(4, encoder.DynamicTableLength);

            // C.5.2
            fields = new HeaderField[] {
                new HeaderField{ Name = ":status", Value ="307", Sensitive = false },
                new HeaderField{ Name = "cache-control", Value ="private", Sensitive = false },
                new HeaderField{ Name = "date", Value ="Mon, 21 Oct 2013 20:13:21 GMT", Sensitive = false },
                new HeaderField{ Name = "location", Value ="https://www.example.com", Sensitive = false },
            };
            result = new Buffer();
            result.AddHexString("4803333037c1c0bf");

            res = encoder.Encode(fields, MaxFrameSize);
            Assert.Equal(result.Bytes, res.Bytes);
            Assert.Equal(4, res.FieldCount);
            Assert.Equal(222, encoder.DynamicTableUsedSize);
            Assert.Equal(4, encoder.DynamicTableLength);

            // C.5.3
            fields = new HeaderField[] {
                new HeaderField{ Name = ":status", Value ="200", Sensitive = false },
                new HeaderField{ Name = "cache-control", Value ="private", Sensitive = false },
                new HeaderField{ Name = "date", Value ="Mon, 21 Oct 2013 20:13:22 GMT", Sensitive = false },
                new HeaderField{ Name = "location", Value ="https://www.example.com", Sensitive = false },
                new HeaderField{ Name = "content-encoding", Value ="gzip", Sensitive = false },
                new HeaderField{ Name = "set-cookie", Value ="foo=ASDJKHQKBZXOQWEOPIUAXQWEOIU; max-age=3600; version=1", Sensitive = false },
            };
            result = new Buffer();
            result.AddHexString(
                "88c1611d4d6f6e2c203231204f637420" +
                "323031332032303a31333a323220474d" +
                "54c05a04677a69707738666f6f3d4153" +
                "444a4b48514b425a584f5157454f5049" +
                "5541585157454f49553b206d61782d61" +
                "67653d333630303b2076657273696f6e" +
                "3d31");

            res = encoder.Encode(fields, MaxFrameSize);
            Assert.Equal(result.Bytes, res.Bytes);
            Assert.Equal(6, res.FieldCount);
            Assert.Equal(215, encoder.DynamicTableUsedSize);
            Assert.Equal(3, encoder.DynamicTableLength);
        }

        [Fact]
        public void ShouldHandleExampleC6OfTheSpecificationCorrectly()
        {
            var encoder = new Encoder(new Encoder.Options{
                HuffmanStrategy = HuffmanStrategy.Always,
                DynamicTableSize = 256,
            });

            var fields = new HeaderField[] {
                new HeaderField{ Name = ":status", Value ="302", Sensitive = false },
                new HeaderField{ Name = "cache-control", Value ="private", Sensitive = false },
                new HeaderField{ Name = "date", Value ="Mon, 21 Oct 2013 20:13:21 GMT", Sensitive = false },
                new HeaderField{ Name = "location", Value ="https://www.example.com", Sensitive = false },
            };

            // C.6.1
            var result = new Buffer();
            result.AddHexString(
                "488264025885aec3771a4b6196d07abe" +
                "941054d444a8200595040b8166e082a6" +
                "2d1bff6e919d29ad171863c78f0b97c8" +
                "e9ae82ae43d3");

            var res = encoder.Encode(fields, MaxFrameSize);
            Assert.Equal(result.Bytes, res.Bytes);
            Assert.Equal(4, res.FieldCount);
            Assert.Equal(222, encoder.DynamicTableUsedSize);
            Assert.Equal(4, encoder.DynamicTableLength);

            // C.6.2
            fields = new HeaderField[] {
                new HeaderField{ Name = ":status", Value ="307", Sensitive = false },
                new HeaderField{ Name = "cache-control", Value ="private", Sensitive = false },
                new HeaderField{ Name = "date", Value ="Mon, 21 Oct 2013 20:13:21 GMT", Sensitive = false },
                new HeaderField{ Name = "location", Value ="https://www.example.com", Sensitive = false },
            };
            result = new Buffer();
            result.AddHexString("4883640effc1c0bf");

            res = encoder.Encode(fields, MaxFrameSize);
            Assert.Equal(result.Bytes, res.Bytes);
            Assert.Equal(4, res.FieldCount);
            Assert.Equal(222, encoder.DynamicTableUsedSize);
            Assert.Equal(4, encoder.DynamicTableLength);

            // C.6.3
            fields = new HeaderField[] {
                new HeaderField{ Name = ":status", Value ="200", Sensitive = false },
                new HeaderField{ Name = "cache-control", Value ="private", Sensitive = false },
                new HeaderField{ Name = "date", Value ="Mon, 21 Oct 2013 20:13:22 GMT", Sensitive = false },
                new HeaderField{ Name = "location", Value ="https://www.example.com", Sensitive = false },
                new HeaderField{ Name = "content-encoding", Value ="gzip", Sensitive = false },
                new HeaderField{ Name = "set-cookie", Value ="foo=ASDJKHQKBZXOQWEOPIUAXQWEOIU; max-age=3600; version=1", Sensitive = false },
            };
            result = new Buffer();
            result.AddHexString(
                "88c16196d07abe941054d444a8200595" +
                "040b8166e084a62d1bffc05a839bd9ab" +
                "77ad94e7821dd7f2e6c7b335dfdfcd5b" +
                "3960d5af27087f3672c1ab270fb5291f" +
                "9587316065c003ed4ee5b1063d5007");

            res = encoder.Encode(fields, MaxFrameSize);
            Assert.Equal(result.Bytes, res.Bytes);
            Assert.Equal(6, res.FieldCount);
            Assert.Equal(215, encoder.DynamicTableUsedSize);
            Assert.Equal(3, encoder.DynamicTableLength);
        }
    }
}
