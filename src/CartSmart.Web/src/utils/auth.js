import { GoogleLogin } from '@react-oauth/google';

export const signInWithGoogle = () => {
  return new Promise((resolve, reject) => {
    const handleSuccess = (response) => {
      resolve({
        credential: response.credential,
        user: response
      });
    };

    const handleError = (error) => {
      reject(error);
    };

    return (
      <GoogleLogin
        onSuccess={handleSuccess}
        onError={handleError}
      />
    );
  });
};

export const signInWithFacebook = async () => {
  // We can implement Facebook login later
  throw new Error('Facebook login not implemented yet');
}; 