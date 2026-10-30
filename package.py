"""Build the distributable ZIP after the plugin has been compiled and tested."""
from scripts.distribution import package

if __name__ == '__main__':
    print(package())
