using System.ComponentModel;
using Xunit;

namespace Finder.Tests
{
    public class BoardInfoTests
    {
        [Fact]
        public void NewBoardInfo_HasEmptyStrings()
        {
            var board = new BoardInfo();

            Assert.Equal(string.Empty, board.Ip);
            Assert.Equal(string.Empty, board.Mac);
            Assert.Equal(string.Empty, board.ClientIp);
            Assert.Equal(string.Empty, board.Version);
            Assert.Equal(string.Empty, board.App);
        }

        [Fact]
        public void SettingNull_CoercesToEmptyString()
        {
            var board = new BoardInfo
            {
                Ip = null!,
                Mac = null!,
                ClientIp = null!,
                Version = null!,
                App = null!
            };

            Assert.Equal(string.Empty, board.Ip);
            Assert.Equal(string.Empty, board.Mac);
            Assert.Equal(string.Empty, board.ClientIp);
            Assert.Equal(string.Empty, board.Version);
            Assert.Equal(string.Empty, board.App);
        }

        [Fact]
        public void SettingValue_RaisesPropertyChangedWithPropertyName()
        {
            var board = new BoardInfo();
            var raised = new List<string?>();
            board.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

            board.Ip = "192.168.1.42";

            Assert.Equal(new[] { nameof(BoardInfo.Ip) }, raised);
        }

        [Fact]
        public void SettingSameValue_StillRaisesPropertyChanged()
        {
            var board = new BoardInfo { Mac = "00:0A:35:11:22:33" };
            int count = 0;
            board.PropertyChanged += (_, _) => count++;

            board.Mac = "00:0A:35:11:22:33";

            Assert.Equal(1, count);
        }

        [Fact]
        public void BoardInfo_ImplementsINotifyPropertyChanged()
        {
            Assert.IsAssignableFrom<INotifyPropertyChanged>(new BoardInfo());
        }
    }
}
